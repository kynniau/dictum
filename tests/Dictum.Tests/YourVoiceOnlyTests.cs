using System.Collections.Concurrent;
using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Voice;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// "Your voice only" (design 1.6 D) through the real controller, state machine and pipeline: a microphone stand-in that cuts when the
/// test says, a speech-engine stand-in that records every part it is given, and <see cref="LevelEmbedder"/>'s fixed vector per
/// synthetic voice (voice 1 is the owner). No model, no real recording.
/// </summary>
public sealed class YourVoiceOnlyTests
{
    private static readonly float[] Print = [1, 0, 0, 0];

    /// <summary>A part of <paramref name="seconds"/> of one synthetic voice (a constant level, see <see cref="SyntheticVoices"/>).</summary>
    private static CapturedAudio Voice(int voice, double seconds) => new(Enumerable.Repeat(voice / 10f, (int)(seconds * 16000)).ToArray(), 0.2f);

    private sealed class Capture : IAudioCapture
    {
        private Action<CapturedAudio, CutReason>? _part;
        public CapturedAudio Rest { get; set; } = CapturedAudio.Empty;
        public bool Cutting => Volatile.Read(ref _part) is not null;
        public float CurrentLevel => 0f;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Start(string? deviceSetting, long keyDownStamp) { }
        public CapturedAudio Stop() { Volatile.Write(ref _part, null); return Rest; }
        public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part) => Volatile.Write(ref _part, part);
        public void Cut(CapturedAudio audio) => Volatile.Read(ref _part)!(audio, CutReason.Pause);
    }

    /// <summary>The speech engine: says which voice it heard ("voice 2"), and keeps every part it was given.</summary>
    private sealed class Engine : ITranscriber
    {
        public ConcurrentQueue<float[]> Heard { get; } = new();
        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            Heard.Enqueue(samples);
            return Task.FromResult($"voice {(int)Math.Round(samples[0] * 10)} spoke");
        }
    }

    private sealed record Rig(DictationController Controller, Capture Capture, Engine Engine, LevelEmbedder Embedder, FakeUi Ui,
        ConcurrentQueue<string> Typed, DictationStateMachine Machine, ListLog Log) : IDisposable
    {
        public void Dispose() => Controller.Dispose();
    }

    private sealed class ListLog : ILog
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Write(LogLevel level, string message) => Lines.Enqueue(message);
    }

    private static Rig Make(bool yourVoiceOnly = true, bool hasPrint = true, IVoiceEmbedder? scorer = null)
    {
        var settings = new AppSettings { Audio = new AudioSettings { YourVoiceOnly = yourVoiceOnly }, Insert = new InsertSettings { TrailingSpace = false } };
        var capture = new Capture();
        var engine = new Engine();
        var embedder = new LevelEmbedder(SyntheticVoices.Distinct());
        var typed = new ConcurrentQueue<string>();
        var log = new ListLog();
        var machine = new DictationStateMachine(StateMachineOptions.Default);
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(engine), () => TranscriptCleaner.RulesOnly("test"),
            (text, token) => { token.ThrowIfCancellationRequested(); typed.Enqueue(text); return Task.CompletedTask; },
            () => settings, log);
        var filter = new OwnVoiceFilter(() => settings, () => hasPrint ? OwnVoiceSource.Ready(Print, scorer ?? embedder) : OwnVoiceSource.Missing(OwnVoiceSource.NoPrint), log);
        var ui = new FakeUi();
        return new Rig(new DictationController(machine, capture, pipeline, ui, () => settings, log, voiceFilter: filter), capture, engine, embedder, ui, typed, machine, log);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>Hold-to-talk: press, let go after half a second; the capture hands back <paramref name="audio"/>.</summary>
    private static void Hold(Rig rig, CapturedAudio audio)
    {
        rig.Capture.Rest = audio;
        var t0 = Environment.TickCount64;
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 500));
    }

    private static async Task StartHandsFree(Rig rig)
    {
        var t0 = Environment.TickCount64;
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 50));
        rig.Controller.Post(new HookEvent(HotkeySignal.Down, t0 + 150));
        rig.Controller.Post(new HookEvent(HotkeySignal.Up, t0 + 200));
        await WaitUntil(() => rig.Capture.Cutting, "hands-free to start cutting");
    }

    private static void StopTap(Rig rig) => rig.Controller.Post(new HookEvent(HotkeySignal.Down, Environment.TickCount64));

    [Fact]
    public async Task Parts_scored_as_another_voice_are_dropped_before_transcription()
    {
        // Held: the whole recording is one part.
        using (var rig = Make())
        {
            Hold(rig, Voice(2, 3));
            await WaitUntil(() => rig.Ui.Pills.Contains(PillView.VoiceSkipped), "the skipped pill");
            Assert.Empty(rig.Engine.Heard); // the speech engine never received it
            Assert.Empty(rig.Typed);
            Assert.Contains("voice filter: skipped 3.0 s", rig.Log.Lines);
            Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("voice 2", StringComparison.Ordinal)); // no words in the log
        }
        // Hands-free: the other voice's part between two of the owner's is never heard, typed or carried as context.
        using (var rig = Make())
        {
            await StartHandsFree(rig);
            rig.Capture.Cut(Voice(1, 3));
            rig.Capture.Cut(Voice(2, 3));
            rig.Capture.Rest = Voice(1, 2);
            StopTap(rig);
            await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
            Assert.Equal([1, 1], rig.Engine.Heard.Select(s => (int)Math.Round(s[0] * 10)));
            Assert.Equal(["Voice 1 spoke.", " Voice 1 spoke."], rig.Typed);
            Assert.Single(rig.Log.Lines, l => l == "voice filter: skipped 3.0 s");
        }
    }

    [Fact]
    public async Task Own_parts_pass()
    {
        using var rig = Make();
        Hold(rig, Voice(1, 3));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Single(rig.Engine.Heard);
        Assert.Equal(["Voice 1 spoke."], rig.Typed);
        Assert.NotEmpty(rig.Embedder.Heard); // it was scored
        Assert.DoesNotContain(PillView.VoiceSkipped, rig.Ui.Pills);
    }

    [Fact]
    public async Task Short_parts_pass_unscored()
    {
        using var rig = Make();
        Hold(rig, Voice(2, 1.0)); // under SpeakerSeparator.MinWindowSeconds: a quick "yes" is never lost
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Single(rig.Engine.Heard);
        Assert.Empty(rig.Embedder.Heard);
        Assert.True(1.0 < SpeakerSeparator.MinWindowSeconds);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Setting_off_or_no_print_means_no_gate(bool yourVoiceOnly, bool hasPrint)
    {
        using var rig = Make(yourVoiceOnly, hasPrint);
        Hold(rig, Voice(2, 3));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done), "the Done pill");
        Assert.Equal(["Voice 2 spoke."], rig.Typed);
        Assert.Empty(rig.Embedder.Heard); // not even scored
    }

    [Fact]
    public async Task Pill_shows_the_skipped_state()
    {
        Assert.Equal("Audio skipped by voice filter", PillText.For(PillView.VoiceSkipped, TimeSpan.Zero));
        Assert.NotNull(PillText.FlashMs(PillView.VoiceSkipped));
        using (var rig = Make())
        {
            Hold(rig, Voice(2, 3));
            await WaitUntil(() => rig.Machine.State == DictationState.Idle && rig.Ui.Pills.Contains(PillView.VoiceSkipped), "the session to end");
            Assert.Equal(PillView.VoiceSkipped, rig.Ui.Pills.Last()); // it says why nothing was typed, then fades
        }
        using (var rig = Make())
        {
            await StartHandsFree(rig);
            rig.Capture.Cut(Voice(2, 3));
            await WaitUntil(() => rig.Ui.Pills.Contains(PillView.VoiceSkipped), "the skipped pill");
            // Still recording: after the brief state the hands-free pill comes back.
            await WaitUntil(() => rig.Ui.Pills.Last() == PillView.RecordingHandsFree, "the hands-free pill again", DictationController.VoiceSkippedShowMs + 3000);
            Assert.Equal(DictationState.RecordingHandsFree, rig.Machine.State);
            StopTap(rig);
            await WaitUntil(() => rig.Machine.State == DictationState.Idle, "the session to end");
            Assert.Empty(rig.Engine.Heard);
        }
    }

    /// <summary>
    /// <see cref="LevelEmbedder"/> on a window that is mostly speech; on a window that is mostly silence, a vector unlike the owner's,
    /// as a real extractor gives no owner-like embedding for room quiet.
    /// </summary>
    private sealed class SilenceSoundsLikeNobody(LevelEmbedder voices) : IVoiceEmbedder
    {
        public float[]? Embed(float[] samples16kMono) =>
            samples16kMono.Count(s => s != 0) * 2 >= samples16kMono.Length ? voices.Embed(samples16kMono) : [0, 0, 1, 0];
    }

    /// <summary>
    /// Fix round F2: the filter scores a part's speech only, as enrolment and --speaker-check --print do (VoiceScore.ScoreSpeech). A
    /// held recording with 3 s of quiet either side of 2.5 s of the owner's voice was mostly silent windows when every window of the
    /// part was scored, and was skipped; scored as speech it passes. Mutation: the filter scoring every window of the part again.
    /// </summary>
    [Fact]
    public async Task An_own_part_with_silence_either_side_passes_the_gate()
    {
        float[] padded = [.. new float[3 * 16000], .. Voice(1, 2.5).Samples, .. new float[3 * 16000]];
        var scorer = new SilenceSoundsLikeNobody(new LevelEmbedder(SyntheticVoices.Distinct()));
        var settings = new AppSettings { Audio = new AudioSettings { YourVoiceOnly = true } };
        Assert.Equal(OwnVoiceVerdict.Own, new OwnVoiceFilter(() => settings, () => OwnVoiceSource.Ready(Print, scorer)).Check(padded));

        using var rig = Make(scorer: scorer);
        Hold(rig, new CapturedAudio(padded, 0.1f));
        await WaitUntil(() => rig.Ui.Pills.Contains(PillView.Done) || rig.Ui.Pills.Contains(PillView.VoiceSkipped), "the part to be decided");
        Assert.DoesNotContain(PillView.VoiceSkipped, rig.Ui.Pills);
        Assert.Single(rig.Engine.Heard); // transcribed, not skipped
    }

    /// <summary>
    /// Fix round F4: the setting on but no gate possible (no speaker model, no print, a print from another model, an unreadable print)
    /// writes one warning per session naming the reason, never words from a dictation, and leaves the setting as the user set it.
    /// </summary>
    [Fact]
    public async Task With_the_setting_on_and_no_gate_one_warning_names_the_reason()
    {
        var settings = new AppSettings { Audio = new AudioSettings { YourVoiceOnly = true } };
        var log = new ListLog();
        var filter = new OwnVoiceFilter(() => settings, () => OwnVoiceSource.Missing(OwnVoiceSource.NoModel), log);
        for (var i = 0; i < 3; i++) Assert.Equal(OwnVoiceVerdict.Off, filter.Check(Voice(2, 3).Samples));
        Assert.Equal(["Your voice only is on, but no dictation can be checked: the speaker model is not downloaded. Everything is transcribed as usual."], log.Lines);
        Assert.True(settings.Audio.YourVoiceOnly); // the switch is not touched

        // Through the controller: two dictations without a print, one warning, both typed.
        using var rig = Make(hasPrint: false);
        Hold(rig, Voice(2, 3));
        await WaitUntil(() => rig.Typed.Count == 1 && rig.Machine.State == DictationState.Idle, "the first dictation");
        Hold(rig, Voice(2, 3));
        await WaitUntil(() => rig.Typed.Count == 2, "the second dictation");
        Assert.Single(rig.Log.Lines, l => l == "Your voice only is on, but no dictation can be checked: there is no voice print. Everything is transcribed as usual.");
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("voice 2", StringComparison.Ordinal));
    }
}
