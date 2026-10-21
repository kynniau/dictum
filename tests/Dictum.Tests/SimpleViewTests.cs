using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
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
        Assert.Contains($"{SimpleView.DownloadSize(ModelCatalog.Find("large-v3-turbo")!.SizeText)} · {SimpleView.MostAccurateSubLine}", shown);
        Assert.Contains($"{SimpleView.DownloadSize(ModelCatalog.Find("medium.en")!.SizeText)} · {SimpleView.MoreAccurateSubLine}", shown);
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
        Assert.Equal(2, lines.Count(l => l == $"In use · {SimpleView.CustomNote}"));
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
        Assert.Equal([SimpleView.TidyLabel, $"{ModelsPage.AddCleanupLabel} (download)"], cleanup);
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
        Assert.Contains($"Downloading 0% · {SimpleView.MoreAccurateSubLine}", Visible(window));
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
}
