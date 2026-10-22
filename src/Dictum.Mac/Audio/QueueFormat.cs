using Dictum.Audio;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Audio;

/// <summary>What <see cref="CaptureBuffer"/> is told about the bytes a queue delivers.</summary>
internal sealed record CaptureFormat(SampleEncoding Encoding, int Channels, int SampleRate);

/// <summary>
/// The formats an input queue is asked for, and how the format it actually records is read. Pure: tested without hardware.
/// </summary>
internal static class QueueFormat
{
    /// <summary>What Whisper wants: 16 kHz mono Float32, so the queue's own converter does the work.</summary>
    public static CoreAudio.StreamDescription Requested => Float(MonoResampler.WhisperRate, 1);

    /// <summary>
    /// The formats to try, in order: <see cref="Requested"/>, then the device's own rate and channel count as Float32 (the
    /// queue still converts the sample type, which every device it opens supports), for CaptureBuffer to downmix and resample.
    /// The second is left out when the HAL gave no nominal rate or channels, or when it is the same as the first.
    /// </summary>
    public static IReadOnlyList<CoreAudio.StreamDescription> Candidates(InputDevice device)
    {
        var list = new List<CoreAudio.StreamDescription> { Requested };
        var rate = device.NominalRate;
        if (rate > 0 && device.Channels > 0 && (rate != MonoResampler.WhisperRate || device.Channels != 1)) list.Add(Float(rate, device.Channels));
        return list;
    }

    /// <summary>Interleaved, packed, native-endian (little on arm64) Float32 linear PCM.</summary>
    public static CoreAudio.StreamDescription Float(double rate, int channels) => new()
    {
        SampleRate = rate,
        FormatID = CoreAudio.FormatLinearPCM,
        FormatFlags = CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked,
        BytesPerPacket = (uint)(4 * channels),
        FramesPerPacket = 1,
        BytesPerFrame = (uint)(4 * channels),
        ChannelsPerFrame = (uint)channels,
        BitsPerChannel = 32,
    };

    /// <summary>
    /// The CaptureBuffer format for what the queue records (kAudioQueueProperty_StreamDescription). Throws
    /// <see cref="NotSupportedException"/> for anything SampleDecoder cannot read: not linear PCM, big-endian, non-interleaved
    /// with more than one channel, unpacked samples, or a sample size other than Float32 / Int16 / Int24 / Int32.
    /// </summary>
    public static CaptureFormat Decode(in CoreAudio.StreamDescription d)
    {
        if (d.FormatID != CoreAudio.FormatLinearPCM) throw new NotSupportedException($"Unsupported audio format: 0x{d.FormatID:X8}, not linear PCM.");
        if ((d.FormatFlags & CoreAudio.FlagIsBigEndian) != 0) throw new NotSupportedException("Unsupported audio format: big-endian samples.");
        if (d.ChannelsPerFrame == 0 || d.SampleRate <= 0) throw new NotSupportedException("Unsupported audio format: no channels or no sample rate.");
        if ((d.FormatFlags & CoreAudio.FlagIsNonInterleaved) != 0 && d.ChannelsPerFrame > 1) throw new NotSupportedException("Unsupported audio format: non-interleaved channels.");
        if (d.BitsPerChannel % 8 != 0 || d.BytesPerFrame != d.BitsPerChannel / 8 * d.ChannelsPerFrame) throw new NotSupportedException($"Unsupported audio format: {d.BitsPerChannel}-bit samples in {d.BytesPerFrame}-byte frames.");
        var isFloat = (d.FormatFlags & CoreAudio.FlagIsFloat) != 0;
        var isSigned = (d.FormatFlags & CoreAudio.FlagIsSignedInteger) != 0;
        var encoding = (isFloat, isSigned, d.BitsPerChannel) switch
        {
            (true, _, 32) => SampleEncoding.Float32,
            (false, true, 16) => SampleEncoding.Int16,
            (false, true, 24) => SampleEncoding.Int24,
            (false, true, 32) => SampleEncoding.Int32,
            _ => throw new NotSupportedException($"Unsupported audio format: {d.BitsPerChannel}-bit {(isFloat ? "float" : isSigned ? "signed" : "unsigned")}."),
        };
        return new CaptureFormat(encoding, (int)d.ChannelsPerFrame, (int)Math.Round(d.SampleRate));
    }

    /// <summary>Bytes in one queue buffer of about <paramref name="milliseconds"/> (whole frames, at least one).</summary>
    public static uint BufferBytes(in CoreAudio.StreamDescription d, int milliseconds) =>
        (uint)Math.Max(1, (int)(d.SampleRate * milliseconds / 1000)) * d.BytesPerFrame;

    /// <summary>
    /// When a packet's first frame was recorded, in ms after key-down; positive means speech in that gap was not captured.
    /// The packet's AudioTimeStamp.mHostTime is in mach_absolute_time ticks (ns = ticks × numer / denom, mach_timebase_info;
    /// 125/3 on the Apple Silicon Mac this was written on). Stopwatch.GetTimestamp on macOS reads the same clock: a scratch
    /// program on 22/10/2026 (macOS 27.2, arm64, .NET 10.0.402) found Stopwatch.Frequency = 1e9 and every Stopwatch stamp
    /// between two bracketing mach_absolute_time reads, within 3 µs. Doubles: well under a microsecond of error at a year of
    /// uptime, and no integer overflow to reason about.
    /// </summary>
    /// <param name="hostTime">The packet's mHostTime, in mach ticks.</param>
    /// <param name="keyDownStamp">Stopwatch.GetTimestamp() at key-down.</param>
    /// <param name="frequency">Stopwatch.Frequency.</param>
    public static double FirstFrameAfterKeyDownMs(ulong hostTime, uint numer, uint denom, long keyDownStamp, long frequency) =>
        (hostTime * (double)numer / denom - keyDownStamp * (1e9 / frequency)) / 1e6;
}
