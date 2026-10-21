using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Onboarding;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>The onboarding window's microphone test (step 5): the level, the advice, the capture's lifetime and the shared microphone words.</summary>
public sealed partial class OnboardingTests
{
    // ---- 5. The microphone test ----

    private static (MicrophoneStep Step, OnboardingWindow Window, LevelCapture Capture, long[] Clock) MicTest(Scratch scratch, AppShell shell, FakePlatform platform, LevelCapture? capture = null)
    {
        var mic = capture ?? new LevelCapture();
        platform.CaptureFactory = () => mic;
        var clock = new long[] { 1_000_000 };
        var (_, window) = Open(shell, scratch, clock);
        var step = GoTo<MicrophoneStep>(window);
        return (step, window, mic, clock);
    }

    private static void Advance(MicrophoneStep step, long[] clock, long ms)
    {
        for (var t = 0L; t < ms; t += 50)
        {
            clock[0] += 50;
            step.Tick();
        }
    }

    /// <summary>Pin: a level above the floor for about three seconds says "Heard you." (and not before).</summary>
    [Fact]
    public Task The_microphone_test_hears_a_level_above_the_floor() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        Assert.True(mic.Open);
        Assert.Contains(OnboardingText.SayWords, Visible(window));
        mic.Level = 0.02f;
        Advance(step, clock, 2_900);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        Advance(step, clock, 150);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: five seconds of silence gives the advice; a sound later can still turn it into "Heard you.".</summary>
    [Fact]
    public Task The_microphone_test_gives_the_advice_after_five_seconds_of_silence() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0f; // a virtual desktop with no microphone redirection: digital silence
        Advance(step, clock, 4_900);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        Advance(step, clock, 150);
        Assert.Equal(OnboardingText.NothingHeard, step.Result);
        mic.Level = 0.05f;
        Advance(step, clock, 3_050);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: with no microphone listed, the no-microphone advice and no capture opened.</summary>
    [Fact]
    public Task With_no_microphone_listed_the_test_says_so_and_opens_nothing() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Devices.Clear();
        var captures = platform.Captures.Count; // the shell's own, made when it was built
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, step.Result);
        Assert.Contains(NoSoundText.NoMicrophoneBalloon, Visible(window));
        Assert.Equal(captures, platform.Captures.Count);
        Assert.False(mic.Open);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: the test's capture is closed on every way out of the step: Next, Skip, Back, and closing the window.</summary>
    [Theory]
    [InlineData("next")]
    [InlineData("skip")]
    [InlineData("back")]
    [InlineData("close")]
    public Task The_test_capture_is_stopped_on_leaving_the_step(string how) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.True(mic.Open);
        Assert.Same(mic, step.Capture);
        switch (how)
        {
            case "next": Click(window.PrimaryButton); break;
            case "skip": Click(window.SecondaryButton); break;
            case "back": Click(window.BackButton); break;
            default: window.Close(); break;
        }
        Dispatcher.UIThread.RunJobs();
        Assert.False(mic.Open, $"the microphone stayed open after {how}");
        Assert.Null(step.Capture);
        if (window.IsVisible) window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: another microphone chosen is tested at once and written at Next; Skip writes nothing.</summary>
    [Fact]
    public Task The_microphone_chosen_is_tested_and_written_at_Next() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Devices.Add(new AudioDeviceInfo("id-2", "Headset", false));
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Null(mic.Device); // the system default first
        Assert.Equal(["System default", "Test microphone (default)", "Headset"], Assert.IsAssignableFrom<IEnumerable<string>>(step.DevicePicker.ItemsSource));
        step.DevicePicker.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Headset", mic.Device);
        Assert.True(mic.Open);
        Next(window);
        Assert.Equal("Headset", SettingsLoader.LoadFile(scratch.Paths.SettingsFile).Settings.Audio.Device);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: with reduced motion the meter moves at most twice a second; otherwise on every look.</summary>
    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 4)]
    public Task The_meter_respects_reduced_motion(bool reduced, int redraws) => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.Flags.Reduced = reduced;
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.1f;
        var before = step.MeterRedraws;
        Advance(step, clock, 2_000);
        Assert.Equal(redraws, step.MeterRedraws - before);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin: a microphone that cannot be opened says why, in Settings > Microphone's words.</summary>
    [Fact]
    public Task A_microphone_that_cannot_be_opened_says_why() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, _, _) = MicTest(scratch, shell, platform, new LevelCapture { Refuse = new InvalidOperationException("Device in use") });
        Assert.Equal(OnboardingText.CouldNotOpen("Device in use"), step.Result);
        window.Close();
        shell.Dispose();
    });

    // ---- Lens fixes (21/10/2026), lens on 6bacce9 ----

    /// <summary>Pin (lens 2): background hiss (0.005, under silence_threshold 0.01) is not "Heard you." and not digital silence either.</summary>
    [Fact]
    public Task Hiss_under_the_silence_threshold_is_not_heard() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.005f;
        Advance(step, clock, 6_000);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        mic.Level = 0.02f;
        Advance(step, clock, 3_050);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 2): the threshold is settings.json's silence_threshold, not a constant.</summary>
    [Fact]
    public Task The_microphone_test_uses_the_settings_silence_threshold() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "silence_threshold": 0.05 }""");
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.02f;
        Advance(step, clock, 3_500);
        Assert.Equal(OnboardingText.SayWords, step.Result);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 7): the test closes the microphone about 5 s after "Heard you." and offers Test again, which opens it again.</summary>
    [Fact]
    public Task The_microphone_closes_after_hearing_you_and_can_test_again() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.05f;
        Advance(step, clock, 3_000);
        Assert.Equal(OnboardingText.HeardYou, step.Result);
        Assert.True(mic.Open);
        Advance(step, clock, (int)MicrophoneStep.StopAfterHeardMs);
        Assert.False(mic.Open);
        Assert.Null(step.Capture);
        Assert.True(step.TestAgainShown);
        Assert.Equal(OnboardingText.HeardYou, step.Result); // the result stays
        Click(All<Button>(step).Single(b => b.Content as string == OnboardingText.TestAgain));
        Assert.True(mic.Open);
        Assert.False(step.TestAgainShown);
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 7): whatever it hears, the test closes the microphone after a minute.</summary>
    [Fact]
    public Task The_microphone_closes_after_a_minute() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.005f; // hiss: never heard, never digital silence
        Advance(step, clock, (int)MicrophoneStep.StopAfterMs - 100);
        Assert.True(mic.Open);
        Advance(step, clock, 100);
        Assert.False(mic.Open);
        Assert.True(step.TestAgainShown);
        Assert.Equal(OnboardingText.NothingHeard, step.Result); // only hiss for a minute: the advice, not a dead "Say a few words." (lens on 36fa43e)
        window.Close();
        shell.Dispose();
    });

    /// <summary>Pin (lens 9): a microphone list that throws (the audio service stopped) is the no-microphone sentence, and nothing is opened.</summary>
    [Fact]
    public Task A_microphone_list_that_throws_says_no_microphone() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        platform.ListDevicesThrows = true;
        var captures = platform.Captures.Count;
        var (step, window, mic, _) = MicTest(scratch, shell, platform);
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, step.Result);
        Assert.Equal(captures, platform.Captures.Count);
        Assert.False(mic.Open);
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// Pin (merge with PR #22): the onboarding's no-microphone sentence and digital-silence floor are now PR #22's own constants,
    /// byte for byte what this lane shipped before the merge, so the words and the rule live in one place.
    /// </summary>
    [Fact]
    public void The_no_microphone_sentence_and_the_floor_are_PR_22s_unchanged()
    {
        Assert.Equal("No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone redirection, then try again.", NoSoundText.NoMicrophoneBalloon);
        Assert.Equal(1e-5f, CaptureBuffer.SoundFloor);
        var check = new MicCheck(0.01);
        Assert.Equal(MicResult.NothingHeard, check.Feed(CaptureBuffer.SoundFloor, MicCheck.NothingAfterMs)); // at the floor: silence
        var sound = new MicCheck(0.01);
        sound.Feed(CaptureBuffer.SoundFloor * 2, MicCheck.NothingAfterMs); // above the floor: not silence (and not heard)
        Assert.Equal(MicResult.Listening, sound.Result);
    }

    // ---- Lens fixes (21/10/2026), lens on 36fa43e ----

    /// <summary>Pin (M2): with silence_threshold 0 (the loader accepts it), digital silence is never "Heard you."</summary>
    [Fact]
    public void A_threshold_of_zero_never_hears_digital_silence()
    {
        var check = new MicCheck(0);
        for (var t = 0; t < 4_000; t += 50) check.Feed(0f, 50);
        Assert.NotEqual(MicResult.Heard, check.Result);
        var floor = new MicCheck(0);
        for (var t = 0; t < 4_000; t += 50) floor.Feed(CaptureBuffer.SoundFloor, 50);
        Assert.NotEqual(MicResult.Heard, floor.Result);
    }

    /// <summary>Pin (M4): one late tick counts as at most two ticks, so three seconds without a look cannot be three seconds of speech.</summary>
    [Fact]
    public Task One_late_tick_is_not_three_seconds_of_speech() => UiSession.Run(() =>
    {
        using var scratch = new Scratch();
        Bundle(scratch);
        var (shell, platform, _) = scratch.Shell();
        var (step, window, mic, clock) = MicTest(scratch, shell, platform);
        mic.Level = 0.02f;
        clock[0] += 3_000;
        step.Tick();
        Assert.Equal(OnboardingText.SayWords, step.Result);
        window.Close();
        shell.Dispose();
    });

    // ---- Final lens (21/10/2026), lens on 4b5ee4f ----

    /// <summary>
    /// Pin (m2): the microphone test's "Nothing heard." and the "No sound from mic" balloon share one advice constant, and
    /// both read byte for byte as before the change.
    /// </summary>
    [Fact]
    public void The_nothing_heard_line_and_the_no_sound_balloon_share_their_advice()
    {
        Assert.EndsWith(NoSoundText.SilentAdvice, OnboardingText.NothingHeard, StringComparison.Ordinal);
        Assert.EndsWith(NoSoundText.SilentAdvice, NoSoundText.SilentBalloon, StringComparison.Ordinal);
        Assert.Equal("Nothing heard. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.", OnboardingText.NothingHeard);
        Assert.Equal("Dictum is not hearing anything. Check that the microphone is plugged in and not muted. On a remote or virtual desktop, microphone redirection must be on.", NoSoundText.SilentBalloon);
    }

    /// <summary>Pin (m2): one builder for the three microphone lists (tray, Settings > Microphone, the onboarding test), labels as before.</summary>
    [Fact]
    public void The_microphone_lists_are_built_in_one_place()
    {
        var (ids, labels) = MicrophoneChoices.Build([new AudioDeviceInfo("1", "Headset", false), new AudioDeviceInfo("2", "Laptop mic", true)], "USB mic");
        Assert.Equal([null, "Headset", "Laptop mic", "USB mic"], ids);
        Assert.Equal(["System default", "Headset", "Laptop mic (default)", "USB mic (not connected)"], labels);
        var (none, plain) = MicrophoneChoices.Build([], null);
        Assert.Equal([null], none);
        Assert.Equal(["System default"], plain);
    }
}
