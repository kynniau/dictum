using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Insert;
using Dictum.Llm;
using Dictum.App;
using Dictum.Personal;
using Dictum.Windows.Input;
using Dictum.Windows.Shell;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Fix batch, item 5's test gaps (#17, #73, #75, #76, #83): behaviour that was right but unpinned.</summary>
public sealed class FixBatchCoverageTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-fixi-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>A microphone that counts how often it was opened and closed.</summary>
    private sealed class CountingCapture : IAudioCapture
    {
        private int _starts;
        private int _stops;

        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public float CurrentLevel => 0f;

        public event Action<Exception>? Failed { add { } remove { } }

        public void Start(string? deviceSetting, long keyDownStamp) => Interlocked.Increment(ref _starts);

        public CapturedAudio Stop()
        {
            Interlocked.Increment(ref _stops);
            return new CapturedAudio(new float[32000], 0.2f);
        }
    }

    private static (DictationController Controller, CountingCapture Capture, ConcurrentQueue<string> Inserted) Rig(StateMachineOptions options)
    {
        var capture = new CountingCapture();
        var inserted = new ConcurrentQueue<string>();
        var settings = new AppSettings();
        var cleaner = TranscriptCleaner.RulesOnly("test");
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber("hello there")), () => cleaner,
            (text, _) => { inserted.Enqueue(text); return Task.CompletedTask; }, () => settings, NullLog.Instance);
        var controller = new DictationController(new DictationStateMachine(options), capture, pipeline, new FakeUi(), () => settings, NullLog.Instance);
        return (controller, capture, inserted);
    }

    [Fact]
    public async Task Esc_while_recording_closes_the_microphone()
    {
        var (controller, capture, _) = Rig(StateMachineOptions.Default);
        using var _ = controller;
        controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));
        Assert.True(await Eventually(() => capture.Starts == 1));
        controller.Post(new HookEvent(HotkeySignal.Escape, Environment.TickCount64));
        Assert.True(await Eventually(() => capture.Stops == 1), $"Esc left the microphone open (stops: {capture.Stops})");
    }

    [Fact]
    public async Task A_lone_tap_closes_the_microphone_when_its_double_tap_window_lapses()
    {
        var (controller, capture, inserted) = Rig(StateMachineOptions.Default);
        using var _ = controller;
        var t0 = Environment.TickCount64;
        controller.Post(new HookEvent(HotkeySignal.Down, t0));
        controller.Post(new HookEvent(HotkeySignal.Up, t0 + 50)); // a tap, then nothing: only the controller's own tick ends it
        Assert.True(await Eventually(() => capture.Stops == 1), $"the pending tap never expired (stops: {capture.Stops})");
        Assert.Empty(inserted);
    }

    [Fact]
    public async Task A_held_key_is_auto_stopped_and_transcribed_at_the_limit()
    {
        var (controller, capture, inserted) = Rig(new StateMachineOptions(300, 400, HandsFreeMaxSeconds: 300, HoldMaxSeconds: 1));
        using var _ = controller;
        controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64)); // and never released
        Assert.True(await Eventually(() => !inserted.IsEmpty, 5000), "auto-stop never fired");
        Assert.Equal(1, capture.Stops);
    }

    [Fact]
    public void The_tray_passes_the_timing_settings_to_the_state_machine_unchanged()
    {
        var settings = new AppSettings { TapMs = 250, DoubleTapMs = 450, HandsFreeMaxSeconds = 120 };
        Assert.Equal(new StateMachineOptions(250, 450, 120), AppRules.Options(settings));
    }

    [Theory]
    [InlineData("hands_free_max_seconds", 0)]
    [InlineData("hands_free_max_seconds", 4)]
    [InlineData("hands_free_max_seconds", 3601)]
    [InlineData("min_recording_ms", -1)]
    public void Timing_settings_out_of_range_use_the_default_and_warn(string key, int value)
    {
        var load = SettingsLoader.Parse($$"""{ "{{key}}": {{value}} }""");
        var defaults = new AppSettings();
        Assert.Equal((defaults.HandsFreeMaxSeconds, defaults.MinRecordingMs), (load.Settings.HandsFreeMaxSeconds, load.Settings.MinRecordingMs));
        Assert.Contains(key, Assert.Single(load.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_network_client_refuses_other_machines_under_offline_only()
    {
        var updateSite = new CountingHandler(); // under the update client's guard: without the guard, the request lands here
        var (providers, downloads, updates) = AppRules.CreateClients(() => true, updateHandler: updateSite);
        using (providers)
        using (downloads)
        using (updates)
        {
            await Assert.ThrowsAsync<OfflineOnlyException>(() => providers.GetAsync("https://dictate-test.invalid/v1", Ct));
            await Assert.ThrowsAsync<OfflineOnlyException>(() => downloads.GetAsync("https://dictate-test.invalid/model.bin", Ct));
            await Assert.ThrowsAsync<OfflineOnlyException>(() => updates.GetAsync(UpdateCheck.VersionUrl, Ct));
        }
        Assert.Equal(0, updateSite.Requests);
    }

    [Fact]
    public void Overlay_windows_get_the_no_activate_click_through_styles_on_every_style_rebuild()
    {
        const uint appWindow = 0x00040000, noActivate = 0x08000000, transparent = 0x00000020, layered = 0x00080000, toolWindow = 0x00000080, topmost = 0x00000008;
        var (style, ex) = new WindowFlags().OverlayStyles(0x16CF0000, appWindow);
        Assert.Equal(0x16CF0000u, style); // the window style itself is untouched
        foreach (var flag in new[] { noActivate, transparent, layered, toolWindow, topmost }) Assert.True((ex & flag) == flag, $"0x{flag:X8}");
        Assert.Equal(0u, ex & appWindow); // never a taskbar button
    }

    [Fact]
    public void A_pasted_transcript_is_kept_out_of_clipboard_history_unless_allowed()
    {
        var kept = Win32Clipboard.Payload("secret", keepOutOfHistory: true);
        Assert.Equal("secret\0", System.Text.Encoding.Unicode.GetString(kept.Single(f => f.Format == "CF_UNICODETEXT").Bytes));
        foreach (var format in Win32Clipboard.PrivacyFormats)
        {
            Assert.Equal(new byte[4], kept.Single(f => f.Format == format).Bytes); // a DWORD 0: "no" to history and to cloud sync
        }
        var allowed = Win32Clipboard.Payload("secret", keepOutOfHistory: false);
        Assert.All(Win32Clipboard.PrivacyFormats, format => Assert.DoesNotContain(allowed, f => f.Format == format));
    }

    [Fact]
    public void The_paste_goes_through_the_profanity_mode_the_settings_choose()
    {
        var paths = new AppPaths(_dir.CreateSubdirectory("cfg").FullName, _dir.CreateSubdirectory("local").FullName, _dir.CreateSubdirectory("exe").FullName);
        File.WriteAllText(paths.ProfanityOverrideFile, "bloody\n");
        File.WriteAllText(Path.Combine(paths.ConfigDir, "corrections.txt"), "teh => the\n");
        var settings = new AppSettings { Profanity = new ProfanitySettings { Mode = ProfanitySettings.Mask } };
        var log = NullLog.Instance;
        var coordinator = new ModelCoordinator(paths, new ModelManager(log), new LocalLlmManager(log), new HttpClient(new CountingHandler()), log, () => settings, null,
            new ScriptedPrompts(), _ => null, () => { }, () => { }, () => { }, (_, _) => { }, gpuMachine: () => false);
        var personal = new PersonalService(paths, log, () => settings, coordinator, reload: () => { }, gpuMachine: () => false);
        personal.Load();
        Assert.Equal("the b***** report", personal.Finish("teh bloody report")); // the correction, then the mask
    }

    [Fact]
    public void The_latency_ceiling_is_two_and_a_half_seconds_per_ten_second_clip()
    {
        Assert.True(CalibrationRanking.WithinCeiling(CalibrationRankingTests.Combo("small.en", 0, 0.05, secondsPerClip: 2.5)));
        Assert.False(CalibrationRanking.WithinCeiling(CalibrationRankingTests.Combo("small.en", 0, 0.05, secondsPerClip: 2.6)));
    }

    [Fact]
    public void A_word_missed_in_half_its_appearances_is_not_suggested()
    {
        string[] script =
        [
            "Please send the invoice to Siobhan before Friday.",
            "Siobhan thinks the shipment arrives on Tuesday.",
            "Remind Siobhan that the lease renews in August.",
            "Siobhan will call the bank tomorrow.",
        ];
        string[] heard =
        [
            "Please send the invoice to Shivon before Friday.",
            "Shivon thinks the shipment arrives on Tuesday.",
            "Remind Siobhan that the lease renews in August.",
            "Siobhan will call the bank tomorrow.",
        ];
        var combo = new ComboResult("small.en", 0, [.. heard.Select((t, i) => new ClipOutcome(i + 1, t, 1, 5, WordErrorRate.Compute(script[i], t)))]);
        Assert.Empty(MissedWords.Find(combo, script, PersonalDictionary.Empty, Corrections.Empty)); // 2 of 4: under two thirds
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
