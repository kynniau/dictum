using NAudio.Wave;

namespace Dictum.Audio;

/// <summary>The microphone, from the platform (Windows: WASAPI through NAudio). Opened per recording, closed after.</summary>
public interface IAudioCapture
{
    /// <summary>Current input level (0-1) for the pill meter; 0 when not recording.</summary>
    float CurrentLevel { get; }

    /// <summary>Opens the device and starts buffering. Throws if the microphone cannot be opened.</summary>
    /// <param name="keyDownStamp">Stopwatch.GetTimestamp() at the key-down, so start and first-packet latency are logged on one clock.</param>
    void Start(string? deviceSetting, long keyDownStamp);

    /// <summary>Stops, releases the device (the system's microphone indicator goes off) and returns the audio.</summary>
    CapturedAudio Stop();

    /// <summary>
    /// Hands-free (SPEC §3 amendment, 16/10/2026): from now until <see cref="Stop"/>, cut the recording at pauses and hand each
    /// finished part to <paramref name="part"/>, in order, on the capture thread (<see cref="CaptureBuffer.SegmentAtPauses"/>);
    /// <see cref="Stop"/> then returns only what follows the last cut. <paramref name="part"/> must not block. A capture that
    /// cannot cut (this default) keeps the whole recording for <see cref="Stop"/>, which is then typed as one part.
    /// </summary>
    void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
    {
    }

    /// <summary>Raised from the capture thread if the device fails mid-recording.</summary>
    event Action<Exception>? Failed;

    /// <summary>
    /// This capture raises <see cref="SoundHeard"/>, so a recording with no sound gets the "No sound from mic" warning
    /// (21/10/2026). A capture that cannot tell (this default) never gets it: its recordings behave as before.
    /// </summary>
    bool ReportsSound => false;

    /// <summary>
    /// Raised once per recording, from the capture thread, at the first 20 ms block louder than
    /// <see cref="CaptureBuffer.SoundFloor"/>. Handlers must not block. This default never raises it.
    /// </summary>
    event Action? SoundHeard
    {
        add { }
        remove { }
    }
}

/// <summary>A microphone the system lists (Settings → Microphone, --selftest).</summary>
/// <param name="Id">The platform's endpoint identifier (audio.device accepts it, or part of the name).</param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public static class AudioFiles
{
    /// <summary>Reads a WAV file as 16 kHz mono float32 (any PCM 16/24/32-bit or float WAV, any rate/channels).</summary>
    public static CapturedAudio ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        var buffer = new CaptureBuffer(SampleDecoder.EncodingOf(format), format.Channels, format.SampleRate);
        var chunk = new byte[format.BlockAlign * 4096];
        int read;
        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Append(chunk.AsSpan(0, read - read % format.BlockAlign));
        }
        return buffer.Seal();
    }
}
