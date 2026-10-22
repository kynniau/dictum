using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Mac.Audio;
using Dictum.Mac.Interop;

namespace Dictum.Tests;

/// <summary>
/// Dictum for macOS, audio (docs/MAC-HANDOVER.md §5, CreateCapture / ListCaptureDevices): device choice, the queue format
/// decision and the first-frame clock are pure and pinned on every OS; listing devices runs on any Mac; a real one-second
/// capture runs only on a Mac with DICTUM_AUDIO_TESTS=1, as the WASAPI test does on Windows.
/// </summary>
public class MacAudioTests(ITestOutputHelper output)
{
    private static readonly InputDevice BuiltIn = new("BuiltInMicrophoneDevice", "MacBook Pro Microphone", IsDefault: true, 1, 48000);
    private static readonly InputDevice Usb = new("AppleUSBAudioEngine:Vendor:Headset:1234:1", "USB Headset", IsDefault: false, 2, 44100);
    // A device whose name contains the other device's UID, so a name match listed first would wrongly win over the exact UID.
    private static readonly InputDevice Tricky = new("Aggregate-1", "Front of BuiltInMicrophoneDevice", IsDefault: false, 1, 48000);

    // ---- Device resolution (CaptureDevices.Resolve) ----

    [Fact]
    public void Resolve_prefers_an_exact_uid_over_an_earlier_name_match()
    {
        var log = new RecordingLog();
        var chosen = CaptureDevices.Resolve([Tricky, Usb, BuiltIn], "BuiltInMicrophoneDevice", log);
        Assert.Same(BuiltIn, chosen);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Resolve_matches_the_uid_without_regard_to_case()
    {
        Assert.Same(Usb, CaptureDevices.Resolve([BuiltIn, Usb], "appleusbaudioengine:vendor:headset:1234:1", new RecordingLog()));
    }

    [Fact]
    public void Resolve_falls_back_to_part_of_the_name_without_regard_to_case()
    {
        Assert.Same(Usb, CaptureDevices.Resolve([BuiltIn, Usb], "headset", new RecordingLog()));
    }

    [Fact]
    public void Resolve_uses_the_default_and_warns_when_the_setting_matches_nothing()
    {
        var log = new RecordingLog();
        var chosen = CaptureDevices.Resolve([Usb, BuiltIn], "Studio Mic", log);
        Assert.Same(BuiltIn, chosen);
        var (level, message) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("\"Studio Mic\" not found", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_uses_the_default_silently_when_nothing_is_configured(string? setting)
    {
        var log = new RecordingLog();
        Assert.Same(BuiltIn, CaptureDevices.Resolve([Usb, BuiltIn], setting, log));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Resolve_throws_the_no_microphone_error_when_there_is_no_input_device()
    {
        var error = Assert.Throws<NoMicrophoneException>(() => CaptureDevices.Resolve([], null, new RecordingLog()));
        Assert.Equal(NoSoundText.NoMicrophoneBalloon, error.Message);
    }

    [Fact]
    public void Resolve_throws_the_no_microphone_error_when_no_device_is_the_default_and_none_matches()
    {
        Assert.Throws<NoMicrophoneException>(() => CaptureDevices.Resolve([Usb with { IsDefault = false }], "nothing", new RecordingLog()));
    }

    // ---- The queue format decision (QueueFormat) ----

    [Fact]
    public void The_queue_is_asked_for_16kHz_mono_float_first_then_the_devices_own_rate_and_channels()
    {
        var formats = QueueFormat.Candidates(Usb);
        Assert.Equal(2, formats.Count);
        Assert.Equal(new CaptureFormat(SampleEncoding.Float32, 1, 16000), QueueFormat.Decode(formats[0]));
        Assert.Equal(new CaptureFormat(SampleEncoding.Float32, 2, 44100), QueueFormat.Decode(formats[1]));
        Assert.Equal(CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked, formats[0].FormatFlags);
        Assert.Equal(4u, formats[0].BytesPerFrame);
        Assert.Equal(8u, formats[1].BytesPerFrame);
    }

    [Fact]
    public void There_is_no_fallback_when_the_device_already_runs_at_16kHz_mono_or_its_rate_is_unknown()
    {
        Assert.Single(QueueFormat.Candidates(new InputDevice("a", "a", true, 1, 16000)));
        Assert.Single(QueueFormat.Candidates(new InputDevice("b", "b", true, 2, 0)));
    }

    public static TheoryData<uint, uint, uint, uint, SampleEncoding> SupportedFormats => new()
    {
        { CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked, 32, 2, 8, SampleEncoding.Float32 },
        { CoreAudio.FlagIsSignedInteger | CoreAudio.FlagIsPacked, 16, 2, 4, SampleEncoding.Int16 },
        { CoreAudio.FlagIsSignedInteger | CoreAudio.FlagIsPacked, 24, 1, 3, SampleEncoding.Int24 },
        { CoreAudio.FlagIsSignedInteger | CoreAudio.FlagIsPacked, 32, 1, 4, SampleEncoding.Int32 },
        { CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked | CoreAudio.FlagIsNonInterleaved, 32, 1, 4, SampleEncoding.Float32 },
    };

    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public void A_recorded_format_maps_to_the_sample_decoders_encoding(uint flags, uint bits, uint channels, uint bytesPerFrame, SampleEncoding expected)
    {
        var format = QueueFormat.Decode(Pcm(48000, flags, bits, channels, bytesPerFrame));
        Assert.Equal(new CaptureFormat(expected, (int)channels, 48000), format);
    }

    private static readonly Dictionary<string, CoreAudio.StreamDescription> Unsupported = new()
    {
        { "big-endian", Pcm(48000, CoreAudio.FlagIsSignedInteger | CoreAudio.FlagIsBigEndian | CoreAudio.FlagIsPacked, 16, 1, 2) },
        { "non-interleaved stereo", Pcm(48000, CoreAudio.FlagIsFloat | CoreAudio.FlagIsNonInterleaved, 32, 2, 4) },
        { "24-bit samples in 32-bit frames", Pcm(48000, CoreAudio.FlagIsSignedInteger, 24, 1, 4) },
        { "unsigned 16-bit", Pcm(48000, CoreAudio.FlagIsPacked, 16, 1, 2) },
        { "64-bit float", Pcm(48000, CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked, 64, 1, 8) },
        { "no channels", Pcm(48000, CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked, 32, 0, 0) },
        { "not linear PCM", Pcm(48000, CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked, 32, 1, 4) with { FormatID = CoreAudio.FourCC("aac ") } },
    };

    public static TheoryData<string> UnsupportedFormats => [.. Unsupported.Keys];

    [Theory]
    [MemberData(nameof(UnsupportedFormats))]
    public void A_format_the_sample_decoder_cannot_read_is_refused(string format)
    {
        var error = Assert.Throws<NotSupportedException>(() => QueueFormat.Decode(Unsupported[format]));
        output.WriteLine($"{format}: {error.Message}");
    }

    [Fact]
    public void A_40ms_buffer_holds_whole_frames()
    {
        Assert.Equal(640u * 4, QueueFormat.BufferBytes(QueueFormat.Requested, 40));
        Assert.Equal(1764u * 8, QueueFormat.BufferBytes(QueueFormat.Float(44100, 2), 40));
    }

    // ---- The first-frame clock (QueueFormat.FirstFrameAfterKeyDownMs) ----

    [Fact]
    public void First_frame_time_converts_mach_ticks_and_stopwatch_ticks_to_one_clock()
    {
        // Apple Silicon's timebase 125/3 (24 MHz ticks) against a 1 GHz Stopwatch: key-down at 1,000 s of uptime, first frame
        // 50 ms later = 1,000,050,000,000 ns = 24,001,200,000 ticks; one 10 ms earlier = 23,999,760,000 ticks.
        Assert.Equal(50.0, QueueFormat.FirstFrameAfterKeyDownMs(24_001_200_000, 125, 3, 1_000_000_000_000, 1_000_000_000), 6);
        Assert.Equal(-10.0, QueueFormat.FirstFrameAfterKeyDownMs(23_999_760_000, 125, 3, 1_000_000_000_000, 1_000_000_000), 6);
        // A 1:1 timebase with a 10 MHz Stopwatch (another clock rate): 1 s of uptime = 1e9 ns host, 1e7 Stopwatch ticks.
        Assert.Equal(0.0, QueueFormat.FirstFrameAfterKeyDownMs(1_000_000_000, 1, 1, 10_000_000, 10_000_000), 6);
    }

    [Fact]
    public void First_frame_time_keeps_sub_millisecond_precision_after_a_long_uptime()
    {
        // About 400 days of uptime in 24 MHz ticks (3.5e16 ns, past the 2^53 a double holds exactly).
        const ulong ticks = 830_000_000_000_000;
        var keyDownNs = (long)(ticks / 3 * 125);
        Assert.Equal(0.0, QueueFormat.FirstFrameAfterKeyDownMs(ticks, 125, 3, keyDownNs, 1_000_000_000), 1);
    }

    // ---- Native layouts read from the SDK headers ----

    [Fact]
    public void Four_character_codes_and_struct_layouts_match_the_sdk_headers()
    {
        Assert.Equal(0x64657623u, CoreAudio.HardwareDevices); // 'dev#'
        Assert.Equal(0x61716364u, CoreAudio.QueueCurrentDevice); // 'aqcd'
        Assert.Equal(0x6C70636Du, CoreAudio.FormatLinearPCM); // 'lpcm'
        Assert.Equal(40, Unsafe.SizeOf<CoreAudio.StreamDescription>());
        Assert.Equal(64, Unsafe.SizeOf<CoreAudio.TimeStamp>());
        Assert.Equal(12, Unsafe.SizeOf<CoreAudio.PropertyAddress>());
        Assert.Equal(8, (int)Marshal.OffsetOf<CoreAudio.QueueBuffer>(nameof(CoreAudio.QueueBuffer.AudioData)));
        Assert.Equal(16, (int)Marshal.OffsetOf<CoreAudio.QueueBuffer>(nameof(CoreAudio.QueueBuffer.AudioDataByteSize)));
    }

    [Fact]
    public void Input_channels_add_up_across_the_buffers_of_a_stream_configuration()
    {
        // AudioBufferList: mNumberBuffers, padding to 8, then 16-byte AudioBuffers {channels, byte size, data pointer}.
        var list = new byte[8 + 2 * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(list, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(24), 2);
        Assert.Equal(3, CaptureDevices.ChannelsInBufferList(list));
        Assert.Equal(0, CaptureDevices.ChannelsInBufferList(new byte[8])); // an output-only device: no input buffers
        Assert.Equal(1, CaptureDevices.ChannelsInBufferList(list.AsSpan(0, 8 + 16))); // a list cut short is read as far as it goes
    }

    [Fact]
    public void An_os_status_shows_its_four_characters_when_printable()
    {
        Assert.Equal("-66680", CoreAudio.Describe(-66680)); // kAudioQueueErr_InvalidDevice
        Assert.Equal("2003329396 ('what')", CoreAudio.Describe(0x77686174));
    }

    // ---- On a Mac ----

    [Fact]
    public void The_device_list_reads_on_a_mac()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Core Audio exists only on macOS.");
        var devices = CaptureDevices.Inputs();
        foreach (var d in devices) output.WriteLine($"{d.Name} | {d.Id} | default={d.IsDefault} | {d.Channels} ch {d.NominalRate:0} Hz");
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        Assert.All(devices, d => Assert.True(d.Channels > 0));
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
        Assert.Equal(devices.Select(d => d.Info), CaptureDevices.List());
    }

    [Fact]
    public void Microphone_capture_starts_and_returns_16kHz_audio_on_a_mac()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("AUDIO_TESTS") == "1", "Set DICTUM_AUDIO_TESTS=1 on a Mac (opens the default microphone for one second; audio stays in memory).");

        var log = new RecordingLog();
        using var capture = new AudioQueueCapture(log);
        var keyDown = Stopwatch.GetTimestamp();
        capture.Start(null, keyDown);
        Thread.Sleep(1000);
        var audio = capture.Stop();
        foreach (var line in log.Lines) output.WriteLine(line.Message);
        output.WriteLine($"captured {audio.Duration.TotalMilliseconds:0} ms, peak block RMS {audio.PeakLevel:0.0000}, nonzero samples {audio.Samples.Count(s => s != 0)}");
        Assert.InRange(audio.Duration.TotalMilliseconds, 700, 1300);
        Assert.Equal(0f, capture.CurrentLevel);
        Assert.Contains(log.Lines, l => l.Message.StartsWith("Capture started in ", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.StartsWith("First audio packet: ", StringComparison.Ordinal));
        Assert.Equal(CapturedAudio.Empty, capture.Stop()); // a second Stop has nothing left
    }

    private static CoreAudio.StreamDescription Pcm(double rate, uint flags, uint bits, uint channels, uint bytesPerFrame) => new()
    {
        SampleRate = rate,
        FormatID = CoreAudio.FormatLinearPCM,
        FormatFlags = flags,
        BytesPerPacket = bytesPerFrame,
        FramesPerPacket = 1,
        BytesPerFrame = bytesPerFrame,
        ChannelsPerFrame = channels,
        BitsPerChannel = bits,
    };
}
