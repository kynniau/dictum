using Dictum.Platform;

namespace Dictum.Audio;

/// <summary>
/// The start and stop sounds (sounds.enabled) as WAV bytes made in memory, so no sound files ship and every platform
/// plays the same two tones (Windows with PlaySound, macOS with NSSound). Moved here unchanged from the Windows build's
/// SoundCues on 23/10/2026; a test pins the bytes.
/// </summary>
public static class CueTones
{
    private const int Rate = 22050;

    /// <summary>The notes (Hz) of each cue: a rising fifth to start, a falling fifth to stop.</summary>
    public static int[] Notes(SoundCue cue) => cue == SoundCue.Start ? [660, 880] : [880, 587];

    /// <summary>A 16-bit mono WAV of 60 ms notes, faded in and out so they do not click, at a fifth of full scale.</summary>
    public static byte[] Wav(int[] notes)
    {
        const int noteSamples = Rate * 60 / 1000;
        var samples = new short[noteSamples * notes.Length];
        for (var n = 0; n < notes.Length; n++)
        {
            for (var i = 0; i < noteSamples; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, noteSamples - i) / (Rate * 0.008));
                samples[n * noteSamples + i] = (short)(0.2 * short.MaxValue * envelope * Math.Sin(2 * Math.PI * notes[n] * i / Rate));
            }
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var data = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);  // PCM
        w.Write((short)1);  // mono
        w.Write(Rate);
        w.Write(Rate * 2);  // bytes per second
        w.Write((short)2);  // block align
        w.Write((short)16); // bits per sample
        w.Write("data"u8);
        w.Write(data);
        foreach (var s in samples) w.Write(s);
        return stream.ToArray();
    }
}
