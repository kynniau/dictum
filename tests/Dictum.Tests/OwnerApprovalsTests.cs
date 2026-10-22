using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// 1.2.1: the owner's decisions of 22/10/2026 (docs/DECISIONS.md, "Owner approvals (22/10/2026)"), each pinned word for word,
/// independently of the app's constants. Headless: nothing reaches the desktop.
/// </summary>
public class OwnerApprovalsTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static List<string> Visible(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    /// <summary>Pumps the UI thread until <paramref name="condition"/> holds; the bound is a ceiling for a hung load, not a timing.</summary>
    private static bool Eventually(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    // ---- Decision 3: the hold pill ----

    /// <summary>Pin (decision 3): the hold pill says "Recording · let go to finish"; hands-free's label is unchanged.</summary>
    [Fact]
    public void The_hold_pill_says_let_go_to_finish()
    {
        Assert.Equal("Recording · let go to finish", PillText.For(PillView.RecordingHold, TimeSpan.Zero));
        Assert.Equal("Recording · let go to finish", PillText.RecordingHold);
        Assert.Equal("Hands-free", PillText.For(PillView.RecordingHandsFree, TimeSpan.Zero));
        Assert.Equal("typing as you pause · tap to stop · Esc stops and keeps what's typed", PillText.HandsFreeHint);
    }

    // ---- Decision 4: no microphone ----

    /// <summary>Pin (decision 4): "Ready" becomes "No microphone" while none is listed; every other state word is kept.</summary>
    [Fact]
    public void The_state_word_is_no_microphone_only_in_place_of_ready()
    {
        Assert.Equal("No microphone", NoSoundText.NoMicrophoneState);
        Assert.Equal(("No microphone", "error"), FlyoutWindow.StateWithNoMicrophone("Ready", "ready", none: true));
        Assert.Equal(("Ready", "ready"), FlyoutWindow.StateWithNoMicrophone("Ready", "ready", none: false));
        foreach (var (state, tone) in new[] { ("Off", "off"), ("Recording", "recording"), ("Working", "working"), ("Downloading", "working"),
                     ("Loading", "working"), ("No model", "error"), ("Model error", "error"), ("Error", "error") })
            Assert.Equal((state, tone), FlyoutWindow.StateWithNoMicrophone(state, tone, none: true));
    }

    /// <summary>
    /// Pin (decision 4): with a speech model ready and no microphone listed the panel's state word is "No microphone", the
    /// no-microphone sentence stays under the header, and once a microphone is listed the next refresh says "Ready" again.
    /// </summary>
    [Fact]
    public Task The_panel_says_no_microphone_until_one_appears() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        var model = Path.Combine(scratch.Root.FullName, "fake-model.bin");
        File.WriteAllText(model, "");
        File.WriteAllText(scratch.Paths.SettingsFile,
            $$"""{ "hotkey": "F24", "whisper": { "model": {{JsonSerializer.Serialize(model)}} }, "llm": { "provider": "none" } }""");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var (shell, platform, _) = scratch.Shell(models);
        shell.Reload(userRequested: false);
        Assert.True(Eventually(() => shell.SpeechReady), "the model never loaded");
        Assert.Equal("Ready", shell.Status.State);

        platform.Devices.Clear();
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var texts = Visible(flyout);
        Assert.Contains("No microphone", texts);
        Assert.DoesNotContain("Ready", texts);
        Assert.Contains(texts, t => t.Split('\n').Contains(NoSoundText.NoMicrophoneBalloon));

        platform.Devices.Add(new AudioDeviceInfo("id-1", "Test microphone", true));
        shell.StatusChanged(); // the panel reads the list again on every refresh
        Dispatcher.UIThread.RunJobs();
        texts = Visible(flyout);
        Assert.Contains("Ready", texts);
        Assert.DoesNotContain("No microphone", texts);
        Assert.DoesNotContain(texts, t => t.Contains(NoSoundText.NoMicrophoneBalloon, StringComparison.Ordinal));
        flyout.Close();
        shell.Dispose();
    });

    // ---- Decision 5: Light | Full in the Advanced view only ----

    /// <summary>Pin (decision 5): the Simple view's panel hides Mode, Light | Full and its line; the Advanced view shows all three.</summary>
    [Theory]
    [InlineData("simple", false)]
    [InlineData("advanced", true)]
    public Task Light_and_Full_are_in_the_advanced_panel_only(string view, bool shown) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "none" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        var segments = All<Avalonia.Controls.Primitives.ToggleButton>(flyout).Where(b => AutomationProperties.GetName(b) is "Mode: Light" or "Mode: Full").ToList();
        Assert.Equal(2, segments.Count);
        Assert.All(segments, b => Assert.Equal(shown, b.IsEffectivelyVisible));
        var texts = Visible(flyout);
        Assert.Equal(shown, texts.Contains("Mode"));
        Assert.Equal(shown, texts.Any(t => t.Contains("Light: ", StringComparison.Ordinal)));
        Assert.Contains("Speech model", texts); // the rest of the panel is still there
        flyout.Close();
        shell.Dispose();
    });

    // ---- Decision 6: "No clean-up" in the header ----

    /// <summary>
    /// Pin (decision 6): with clean-up off the panel header's clean-up part is "No clean-up" in both views (and the icon's
    /// tooltip with it); the choice keeps its name "Off", and Settings > Models' "In use:" line keeps "no clean-up".
    /// </summary>
    [Theory]
    [InlineData("simple")]
    [InlineData("advanced")]
    public Task The_header_says_No_clean_up_when_clean_up_is_off(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }, "whisper": { "model": "base.en" }, "llm": { "provider": "off" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Contains(" · No clean-up · ", shell.Status.Models, StringComparison.Ordinal);
        Assert.Contains(" · No clean-up · ", shell.Status.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(" · Off · ", shell.Status.Models, StringComparison.Ordinal);
        var flyout = new FlyoutWindow(shell);
        flyout.ShowNear(null);
        Assert.Contains(Visible(flyout), t => t.Contains(" · No clean-up · ", StringComparison.Ordinal));
        flyout.Close();

        Assert.Equal("No clean-up", SimpleView.OffHeaderLabel);
        Assert.Equal("Off", SimpleView.OffLabel);
        Assert.Equal("Off (paste what was heard)", SimpleView.OffAdvancedLabel);
        Assert.Equal("no clean-up", shell.Models.CurrentCleanupName);
        shell.Dispose();
    });
}
