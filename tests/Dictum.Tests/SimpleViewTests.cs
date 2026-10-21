using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>
/// Dictum 1.2 (product owner, 21/10/2026): Settings > Models and the tray panel's pickers default to a Simple view of three plain
/// speech tiers and "Tidy my words"; the full catalogue is the Advanced view, one click away (ui.view).
/// </summary>
public sealed class SimpleViewTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    /// <summary>The words a person can see: text blocks that are shown, inside shown parents.</summary>
    private static string Visible(ILogical root) => string.Join("\n", All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    private static Button Named(ILogical root, string name) => All<Button>(root).Single(b => AutomationProperties.GetName(b) == name);

    private static ToggleButton Segment(ILogical root, string label) => All<ToggleButton>(root).Single(b => AutomationProperties.GetName(b) == $"{ModelsPage.ViewName}: {label}");

    private static List<string> Labels(FlyoutWindow flyout, string picker) =>
        [.. Assert.IsAssignableFrom<IEnumerable<string>>(All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == picker).ItemsSource)];

    private static SettingsWindow ModelsWindow(Dictum.UI.Shell.AppShell shell)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        return window;
    }

    /// <summary>Pin: with no ui key the view is Simple, in the settings, on the Models page and in the default settings file.</summary>
    [Fact]
    public Task The_default_view_is_simple() => UiSession.Run(() =>
    {
        Assert.Equal(UiSettings.Simple, SettingsLoader.Parse("{}").Settings.Ui.View);
        Assert.Equal(UiSettings.Simple, SettingsLoader.Parse(SettingsFile.DefaultJson("base.en")).Settings.Ui.View);

        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        Assert.True(Segment(window, SimpleView.SimpleLabel).IsChecked);
        Assert.False(Segment(window, SimpleView.AdvancedLabel).IsChecked);
        var shown = Visible(window);
        Assert.Contains(SimpleView.SwitchHelp, shown);
        Assert.Contains(SimpleView.Explainer, shown);
        foreach (var label in new[] { SimpleView.StandardLabel, SimpleView.MoreAccurateLabel, SimpleView.MostAccurateLabel, SimpleView.TidyTitle })
            Assert.Contains(label, shown.Split('\n'));
        Assert.DoesNotContain("tiny.en", shown); // no catalogue row
        Assert.DoesNotContain("Beam search", shown); // decoding is an Advanced detail
        Assert.Contains($"In use · {SimpleView.StandardSubLineNotIncluded}", shown); // base.en did not come with this scratch copy: no "Included"
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the Advanced segment writes ui.view through the settings writer; the page shows the catalogue, and a new session opens in Advanced.</summary>
    [Fact]
    public Task Switching_the_view_persists_and_survives_a_reload() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var advanced = Segment(window, SimpleView.AdvancedLabel);
        advanced.IsChecked = true; // what a click on it does
        Assert.Equal(UiSettings.Advanced, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Ui.View); // written
        Assert.Equal(UiSettings.Advanced, shell.Settings.Ui.View); // and reloaded
        Assert.True(advanced.IsChecked);
        Assert.False(Segment(window, SimpleView.SimpleLabel).IsChecked);
        var shown = Visible(window);
        Assert.Contains($"tiny.en · {ModelCatalog.Find("tiny.en")!.SizeText}", shown);
        Assert.DoesNotContain(SimpleView.Explainer, shown);
        window.Close();
        shell.Dispose();

        var (again, _, _) = scratch.Shell(); // the next session reads the same file
        again.Reload(userRequested: false);
        Assert.Equal(UiSettings.Advanced, again.Settings.Ui.View);
        var reopened = ModelsWindow(again);
        Assert.True(Segment(reopened, SimpleView.AdvancedLabel).IsChecked);
        Assert.Contains("tiny.en · ", Visible(reopened));

        Segment(reopened, SimpleView.SimpleLabel).IsChecked = true; // and back
        Assert.Equal(UiSettings.Simple, SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Ui.View);
        Assert.Contains(SimpleView.Explainer, Visible(reopened));
        reopened.Close();
        again.Dispose();
    });

    /// <summary>Pin: an unknown ui.view warns and falls back to Simple, like every other setting.</summary>
    [Fact]
    public void An_unknown_view_warns_and_uses_simple()
    {
        var load = SettingsLoader.Parse("""{ "ui": { "view": "expert" } }""");
        Assert.Equal(UiSettings.Simple, load.Settings.Ui.View);
        Assert.Contains(load.Warnings, w => w.Contains("\"ui.view\"", StringComparison.Ordinal));
        Assert.Equal(UiSettings.Advanced, SettingsLoader.Parse("""{ "ui": { "view": "Advanced" } }""").Settings.Ui.View);
    }

    /// <summary>Pin: the tier → model mapping per machine class, read from the catalogue's recommendation and the Mode presets.</summary>
    [Fact]
    public void The_tiers_map_to_the_models_the_code_already_recommends()
    {
        Assert.Equal(["base.en", "small.en", "medium.en"], SimpleView.Tiers(gpuMachine: false).Select(t => t.Model));
        Assert.Equal(["base.en", "medium.en", "large-v3-turbo"], SimpleView.Tiers(gpuMachine: true).Select(t => t.Model));
        foreach (var gpu in new[] { false, true })
        {
            var tiers = SimpleView.Tiers(gpu);
            Assert.Equal([SpeechTier.Standard, SpeechTier.MoreAccurate, SpeechTier.MostAccurate], tiers.Select(t => t.Tier));
            Assert.Equal([SimpleView.StandardLabel, SimpleView.MoreAccurateLabel, SimpleView.MostAccurateLabel], tiers.Select(t => t.Label));
            Assert.All(tiers, t => Assert.NotNull(ModelCatalog.Find(t.Model))); // every tier is a catalogue entry
            Assert.Equal(ModelCatalog.Bundled, tiers[0].Model); // Standard is the model that comes with Dictum
            Assert.Equal(ModePresets.For(ModePreset.Light, gpu).SpeechModel, tiers[0].Model);
            Assert.Contains(ModePresets.For(ModePreset.Full, gpu).SpeechModel, tiers.Skip(1).Select(t => t.Model)); // Full's model is a tier above Standard
            Assert.Contains(ModelCatalog.Recommend(gpu).Model, tiers.Select(t => t.Model)); // and so is the catalogue's recommendation
            Assert.True(ModelCatalog.Find(tiers[1].Model)!.Bytes < ModelCatalog.Find(tiers[2].Model)!.Bytes); // each tier a bigger model
            Assert.True(ModelCatalog.Find(tiers[0].Model)!.Bytes < ModelCatalog.Find(tiers[1].Model)!.Bytes);
        }
        Assert.Equal(SpeechTier.MostAccurate, SimpleView.TierOf("medium.en", gpuMachine: false)!.Tier);
        Assert.Equal(SpeechTier.MoreAccurate, SimpleView.TierOf("medium.en", gpuMachine: true)!.Tier);
        Assert.Null(SimpleView.TierOf("tiny.en", gpuMachine: false));
    }

    /// <summary>Pin: on a GPU machine the page lists the GPU mapping, and choosing Most accurate takes the existing Select path (asked first).</summary>
    [Fact]
    public Task A_gpu_machine_lists_its_own_tiers_and_choosing_one_uses_the_select_path() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var server = new CountingHandler();
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: server, gpuMachine: true);
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var shown = Visible(window);
        Assert.Contains($"{ModelCatalog.Find("large-v3-turbo")!.SizeText} · {SimpleView.MostAccurateSubLine}", shown); // the size alone: the line may say "download"
        Assert.Contains($"{ModelCatalog.Find("medium.en")!.SizeText} · {SimpleView.MoreAccurateSubLine}", shown);
        Named(window, SimpleView.ActionName("Download…", SimpleView.MostAccurateLabel)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["ggml-large-v3-turbo.bin"], dialogs.DownloadQuestions); // the catalogue's own question, for the GPU tier
        Assert.Equal(0, server.Requests); // declined: nothing fetched, nothing changed
        Assert.Equal("base.en", shell.Settings.Whisper.Model);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the bundled base.en makes Standard "Included" (its line, no state before it) with a Use button named for the tier.</summary>
    [Fact]
    public Task The_bundled_model_makes_standard_included() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" } }""");
        Directory.CreateDirectory(scratch.Paths.BundledModelsDir);
        var bundled = Path.Combine(scratch.Paths.BundledModelsDir, "ggml-base.en.bin");
        using (var file = new FileStream(bundled, FileMode.Create, FileAccess.Write)) file.SetLength(ModelCatalog.Find("base.en")!.Bytes);
        var (shell, _, _) = scratch.Shell();
        new Dictum.Transcribe.ModelIntegrity(scratch.Paths.VerifiedModelsFile, NullLog.Instance).Remember(bundled);
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var lines = Visible(window).Split('\n');
        Assert.Contains(SimpleView.StandardSubLine, lines);
        Assert.True(Named(window, SimpleView.ActionName("Use", SimpleView.StandardLabel)).IsEnabled);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: a model chosen in Advanced that is none of the tiers shows in Simple as "Custom: name", in the page and the tray.</summary>
    [Fact]
    public Task An_off_tier_model_shows_as_a_custom_row() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "tiny.en" }, "llm": { "provider": "local", "model": "qwen2.5-0.5b" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var lines = Visible(window).Split('\n');
        Assert.Contains(SimpleView.Custom("tiny.en"), lines);
        Assert.Contains(SimpleView.Custom("qwen2.5-0.5b"), lines);
        Assert.Equal(2, lines.Count(l => l == "In use. Not one of the choices above.")); // not "Chosen in Advanced view": calibration, presets or --model may have chosen it
        Assert.DoesNotContain($"In use · {SimpleView.TidySubLine}", lines); // the Tidy row is not in use

        Named(window, SimpleView.ActionName("Use", SimpleView.TidyLabel)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(LlmSettings.NoProvider, shell.Settings.Llm.Provider); // the recommended model is not on this PC: the rule-based tidy
        window.Close();

        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal(SimpleView.Custom("tiny.en"), Labels(flyout, "Speech model")[0]);
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the Simple view offers no cloud clean-up, on the page or in the tray; the Advanced view still points to it.</summary>
    [Fact]
    public Task The_simple_view_never_shows_cloud() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        string[] cloudWords = ["cloud", "Cloud", "Anthropic", "Claude", .. CloudPresets.All.Select(p => p.Name)];
        var window = ModelsWindow(shell);
        // The Language card (in both views) cites OpenAI's README about translation; that line names the speech model's
        // author, not the cloud preset, so it is left out of the check.
        var simple = string.Join("\n", Visible(window).Split('\n').Where(l => !l.StartsWith("Writes English whatever you speak.", StringComparison.Ordinal)));
        Assert.All(cloudWords, w => Assert.DoesNotContain(w, simple));
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var cleanup = Labels(flyout, "Clean-up");
        Assert.Equal([SimpleView.TidyLabel, $"{ModelsPage.AddCleanupLabel} (download)", SimpleView.OffLabel], cleanup);
        Assert.All(cloudWords, w => Assert.DoesNotContain(cleanup, l => l.Contains(w, StringComparison.Ordinal)));
        flyout.Close();

        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        window.ShowPage("Storage");
        window.ShowPage("Models");
        Assert.Contains("Cloud clean-up is switched on in Clean-up, after a consent step.", Visible(window)); // Advanced keeps the pointer
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the tray panel's speech picker shows the tier names in Simple and the model names in Advanced.</summary>
    [Fact]
    public Task The_tray_picker_follows_the_view() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal([$"{SimpleView.StandardLabel} (download)", $"{SimpleView.MoreAccurateLabel} (download)", $"{SimpleView.MostAccurateLabel} (download)"],
            Labels(flyout, "Speech model")); // nothing is on this PC in the scratch profile

        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced))); // the panel refreshes on the reload
        Dispatcher.UIThread.RunJobs();
        var advanced = Labels(flyout, "Speech model");
        Assert.Equal(ModelCatalog.Entries.Count, advanced.Count);
        Assert.Equal("tiny.en (download)", advanced[0]);
        Assert.Contains(advanced, l => l.StartsWith("large-v3-turbo", StringComparison.Ordinal));
        Assert.DoesNotContain(advanced, l => l.StartsWith(SimpleView.StandardLabel, StringComparison.Ordinal));
        Assert.Contains("Rule-based tidy", Labels(flyout, "Clean-up"));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Pin: a download in progress shows as Downloading in both views, page and tray, with the Simple button disabled and named for why.</summary>
    [Fact]
    public Task A_download_in_progress_shows_in_both_views() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var server = new GateHandler([1, 2, 3, 4]);
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: server);
        shell.Reload(userRequested: false);
        dialogs.ConfirmDownloads = true;
        shell.Models.Select("small.en"); // More accurate on a CPU
        for (var waited = 0; server.InFlight != 1 && waited < 5000; waited += 10) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(server.InFlight == 1, "small.en never started downloading");

        var window = ModelsWindow(shell);
        var busy = All<Button>(window).Single(b => b.Content as string == ModelsPage.DownloadingButton);
        Assert.False(busy.IsEnabled);
        Assert.Equal($"{SimpleView.MoreAccurateLabel} is already downloading", AutomationProperties.GetName(busy));
        Assert.Contains($"Downloading 0% · {SimpleView.CpuMoreAccurateSubLine}", Visible(window));
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(SimpleView.MoreAccurateLabel + FlyoutWindow.DownloadingMark, Labels(flyout, "Speech model"));
        flyout.Close();

        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        busy = All<Button>(window).Single(b => b.Content as string == ModelsPage.DownloadingButton);
        Assert.Equal("small.en is already downloading", AutomationProperties.GetName(busy));
        Assert.Contains($"Downloading 0% · {ModelCatalog.Find("small.en")!.TradeOff}", Visible(window));
        flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains("small.en" + FlyoutWindow.DownloadingMark, Labels(flyout, "Speech model"));
        flyout.Close();

        Assert.Equal(0, server.Cancelled); // switching the view cancels nothing
        Assert.True(shell.Models.IsDownloading);
        window.Close();
        shell.Dispose();
        return true;
    });

    /// <summary>Pin: every Simple control is reached with Tab and named, and a progress refresh keeps the focused row's button (and its focus).</summary>
    [Fact]
    public Task Simple_controls_are_named_and_keep_focus_on_refresh() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        foreach (var name in new[]
                 {
                     $"{ModelsPage.ViewName}: {SimpleView.SimpleLabel}", $"{ModelsPage.ViewName}: {SimpleView.AdvancedLabel}",
                 })
        {
            var segment = All<ToggleButton>(window).Single(b => AutomationProperties.GetName(b) == name);
            Assert.True(segment.Focusable && segment.IsTabStop, name);
        }
        var more = Named(window, SimpleView.ActionName("Download…", SimpleView.MoreAccurateLabel));
        Assert.True(more.Focusable && more.IsTabStop && more.IsEffectivelyVisible);
        Assert.True(Named(window, SimpleView.ActionName("Download…", SimpleView.MostAccurateLabel)).IsTabStop);
        var calibrate = All<Button>(window).Single(b => b.Content as string == ModelsPage.CalibrateLabel);
        Assert.True(calibrate.IsEffectivelyVisible && calibrate.IsTabStop);

        window.Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.True(more.Focus(NavigationMethod.Tab));
        for (var i = 0; i < 3; i++) shell.StatusChanged(); // what each progress tick does
        Dispatcher.UIThread.RunJobs();
        Assert.Same(more, Named(window, SimpleView.ActionName("Download…", SimpleView.MoreAccurateLabel))); // not rebuilt
        Assert.True(more.IsFocused);
        window.Close();
        shell.Dispose();
    });

    // ---- Lens fixes (21/10/2026), first lens on 1316ea6 ----

    private static void Press(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>Lens 2: the tier lines follow the machine class; on a CPU they say what the measured times say.</summary>
    [Fact]
    public void The_tier_lines_follow_the_machine_class()
    {
        Assert.Equal([SimpleView.StandardSubLine, "Bigger download. Takes a few seconds longer on this PC.", "Slow without a graphics card: can take longer than you spoke."],
            SimpleView.Tiers(gpuMachine: false).Select(t => t.SubLine));
        Assert.Equal([SimpleView.StandardSubLine, "Bigger download, a bit slower.", "Best results. Needs a strong PC or patience."],
            SimpleView.Tiers(gpuMachine: true).Select(t => t.SubLine));
        Assert.Equal(SimpleView.CpuMostAccurateSubLine, SimpleView.Tiers(gpuMachine: false, "es")[1].SubLine); // medium on a CPU is the slow one
    }

    /// <summary>Lens 1: a language other than English maps the tiers to multilingual models; a tier the language rules out carries a guard.</summary>
    [Fact]
    public void A_language_other_than_english_maps_the_tiers_to_multilingual_models()
    {
        Assert.Equal(["small", "medium"], SimpleView.Tiers(false, "es").Select(t => t.Model)); // nothing above medium on a CPU
        Assert.Equal(["small", "medium", "large-v3-turbo"], SimpleView.Tiers(true, "de").Select(t => t.Model));
        Assert.Equal(["small", "medium", "large-v3"], SimpleView.Tiers(true, "es", translate: true).Select(t => t.Model)); // turbo cannot translate
        Assert.Equal(["small", "medium"], SimpleView.Tiers(false, "en", translate: true).Select(t => t.Model));
        Assert.Equal(["small", "medium", "large-v3-turbo"], SimpleView.Tiers(true, "auto").Select(t => t.Model));
        foreach (var (gpu, language, translate) in new[] { (false, "es", false), (true, "de", false), (true, "es", true), (false, "en", true), (true, "auto", false), (false, "en", false), (true, "en", false) })
        {
            Assert.All(SimpleView.Tiers(gpu, language, translate), t =>
            {
                Assert.Null(t.Guard);
                Assert.Null(Dictum.Transcribe.WhisperLanguages.Problem(t.Model, language, translate)); // every tier can do what the settings ask
            });
        }
        // An English-only model is a tier only for plain English, so its guard is the existing problem sentence (lens on 787b300).
        Assert.Equal(Dictum.Transcribe.WhisperLanguages.Problem("base.en", "es", false), SimpleView.GuardLine("base.en", "es", false));
        Assert.NotNull(SimpleView.GuardLine("small.en", "auto", false));
        Assert.Null(SimpleView.GuardLine("base.en", "en", false));
        Assert.Null(SimpleView.GuardLine("small", "es", false));
        Assert.Equal(Dictum.Transcribe.WhisperLanguages.Problem("small", "yue", false), SimpleView.Tiers(false, "yue")[0].Guard); // Cantonese: large-v3 only
        Assert.Equal(SpeechTier.Standard, SimpleView.TierOf("small", false, "es")!.Tier);
        Assert.Null(SimpleView.TierOf("base.en", false, "es"));
    }

    /// <summary>Lens 1: with Spanish and the multilingual small in use, nothing on the page switches to an English-only model.</summary>
    [Fact]
    public Task A_user_whose_language_is_not_english_is_never_switched_to_an_english_only_model() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "es" }, "llm": { "provider": "none" } }""");
        Directory.CreateDirectory(scratch.Paths.DefaultModelsDir); // base.en on this PC: the English Standard would be one press away
        using (var file = new FileStream(Path.Combine(scratch.Paths.DefaultModelsDir, "ggml-base.en.bin"), FileMode.Create, FileAccess.Write)) file.SetLength(ModelCatalog.Find("base.en")!.Bytes);
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        foreach (var standard in All<Button>(window).Where(b => AutomationProperties.GetName(b) is "Use Standard" or "Download Standard").ToList()) Press(standard);
        Assert.Equal("small", shell.Settings.Whisper.Model);
        Assert.Empty(dialogs.DownloadQuestions);
        Assert.Contains($"In use · {SimpleView.StandardSubLineNotIncluded}", Visible(window)); // Standard is small, the model in use
        Assert.DoesNotContain(SimpleView.Custom("small"), Visible(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Lens 1: the tray lists the same multilingual tiers, and choosing one asks in plain words.</summary>
    [Fact]
    public Task The_tray_lists_the_multilingual_tiers() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "es" }, "llm": { "provider": "none" } }""");
        var (shell, _, dialogs) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal([$"{SimpleView.StandardLabel} (download)", $"{SimpleView.MoreAccurateLabel} (download)"], Labels(flyout, "Speech model"));
        All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Speech model").SelectedIndex = 1;
        Assert.Equal(["ggml-medium.bin"], dialogs.DownloadQuestions);
        Assert.Equal($"Download {SimpleView.MoreAccurateLabel} ({ModelCatalog.Find("medium")!.SizeText})? {ModelPromptText.PlainCheck}.", dialogs.PlainQuestions.Single().Split("\n\n")[0]);
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Lens 1: a tier the language rules out (Cantonese on small and medium) has no button on the page and is not offered in the tray.</summary>
    [Fact]
    public Task A_tier_the_language_rules_out_is_never_offered() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en", "language": "yue" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var lines = Visible(window).Split('\n');
        Assert.Contains(Dictum.Transcribe.WhisperLanguages.Problem("small", "yue", false)!, lines);
        Assert.Contains(Dictum.Transcribe.WhisperLanguages.Problem("medium", "yue", false)!, lines);
        Assert.DoesNotContain(All<Button>(window), b => AutomationProperties.GetName(b) is { } n && (n.EndsWith(SimpleView.StandardLabel, StringComparison.Ordinal) || n.EndsWith(SimpleView.MoreAccurateLabel, StringComparison.Ordinal)));
        Assert.Contains(SimpleView.Custom("base.en"), lines);
        window.Close();
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal([SimpleView.Custom("base.en")], Labels(flyout, "Speech model"));
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Lens 5: with text that is not English the clean-up tidies spacing only, and the Tidy line says so.</summary>
    [Fact]
    public Task The_tidy_line_says_spacing_only_for_a_language_other_than_english() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "es" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        Assert.Contains("In use · Tidies spacing only for Spanish.", Visible(window).Split('\n'));
        Assert.DoesNotContain(SimpleView.TidySubLine, Visible(window));
        window.Close();
        shell.Dispose();
    });

    /// <summary>Lens 3: a Simple choice asks in plain words (and names the running download by its tier), Advanced keeps the catalogue question.</summary>
    [Fact]
    public Task A_simple_choice_asks_in_plain_words() => UiSession.RunAsync(async () =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var server = new GateHandler([1, 2, 3, 4]);
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: server);
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var more = Named(window, SimpleView.ActionName("Download…", SimpleView.MoreAccurateLabel));
        Press(more); // declined
        var question = Assert.Single(dialogs.PlainQuestions);
        Assert.StartsWith($"Download More accurate ({ModelCatalog.Find("small.en")!.SizeText})? It comes from the model makers' official download page and is checked before use.", question);
        Assert.DoesNotContain("ggml-", question);
        Assert.DoesNotContain(ModelCatalog.DownloadHost, question);

        dialogs.ConfirmDownloads = true;
        Press(more);
        for (var waited = 0; server.InFlight != 1 && waited < 5000; waited += 10) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(server.InFlight == 1, "small.en never started downloading");
        Press(Named(window, SimpleView.ActionName("Download…", SimpleView.MostAccurateLabel))); // the busy question, answered Cancel
        Assert.StartsWith($"More accurate is downloading (", dialogs.PlainQuestions[^1]);
        Assert.Contains($"Download Most accurate ({ModelCatalog.Find("medium.en")!.SizeText}) after it", dialogs.PlainQuestions[^1]);

        var asked = dialogs.PlainQuestions.Count;
        shell.Write(("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        Press(All<Button>(window).First(b => b.Content as string == "Download…")); // tiny.en, the first catalogue row
        Assert.Equal("ggml-tiny.en.bin", dialogs.DownloadQuestions[^1]); // the catalogue question
        Assert.Equal(asked, dialogs.PlainQuestions.Count);
        Assert.Equal(0, server.Cancelled);
        window.Close();
        shell.Dispose();
        return true;
    });

    /// <summary>Lens 6: a long entry in a closed tray picker ends in an ellipsis instead of being cut off.</summary>
    [Fact]
    public Task A_long_tray_entry_is_trimmed_with_an_ellipsis() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "large-v3-turbo-q5_0" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        flyout.UpdateLayout();
        var speech = All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Speech model");
        var shown = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(speech).OfType<TextBlock>().Single(t => t.Text == SimpleView.Custom("large-v3-turbo-q5_0"));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, shown.TextTrimming);
        Assert.True(shown.Bounds.Width <= speech.Bounds.Width, $"{shown.Bounds.Width:0} of {speech.Bounds.Width:0}");
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>Lens 7: choosing "Tidy my words" in the tray's Simple clean-up picker switches a Custom clean-up to the rule-based tidy.</summary>
    [Fact]
    public Task Choosing_tidy_my_words_in_the_tray_switches_to_the_rule_based_tidy() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "base.en" }, "llm": { "provider": "local", "model": "qwen2.5-0.5b" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Equal([SimpleView.Custom("qwen2.5-0.5b"), SimpleView.TidyLabel, SimpleView.OffLabel], Labels(flyout, "Clean-up"));
        All<ComboBox>(flyout).Single(c => AutomationProperties.GetName(c) == "Clean-up").SelectedIndex = 1;
        Assert.Equal(LlmSettings.NoProvider, shell.Settings.Llm.Provider); // the recommended model is not on this PC: the rule-based tidy
        flyout.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Lens on 787b300: "Add AI clean-up" is not offered in the Simple view, page or tray, while the text is not English (no
    /// clean-up model runs on it); English keeps it, and so does the Advanced view.
    /// </summary>
    [Fact]
    public Task Add_ai_clean_up_is_not_offered_in_simple_for_text_that_is_not_english() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small", "language": "es" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var window = ModelsWindow(shell);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Button AddAi() => All<Button>(window).Single(b => b.Content is string c && c.StartsWith(ModelsPage.AddCleanupLabel, StringComparison.Ordinal));
        Assert.False(AddAi().IsEffectivelyVisible);
        Assert.Equal([SimpleView.TidyLabel, SimpleView.OffLabel], Labels(flyout, "Clean-up"));

        shell.Write(("whisper.language", System.Text.Json.Nodes.JsonValue.Create("en")));
        Dispatcher.UIThread.RunJobs();
        Assert.True(AddAi().IsEffectivelyVisible);
        Assert.Contains(Labels(flyout, "Clean-up"), l => l.StartsWith(ModelsPage.AddCleanupLabel, StringComparison.Ordinal));

        shell.Write(("whisper.language", System.Text.Json.Nodes.JsonValue.Create("es")), ("ui.view", System.Text.Json.Nodes.JsonValue.Create(UiSettings.Advanced)));
        Dispatcher.UIThread.RunJobs();
        Assert.True(AddAi().IsEffectivelyVisible); // the Advanced view keeps it, with every other detail
        flyout.Close();
        window.Close();
        shell.Dispose();
    });
}
