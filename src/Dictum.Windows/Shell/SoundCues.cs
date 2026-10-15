using System.Runtime.InteropServices;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Shell;

/// <summary>
/// The start and stop sounds (sounds.enabled): two short tones made in memory (no sound files ship), played with winmm's
/// PlaySound from unmanaged memory that lives as long as the process, because an asynchronous SND_MEMORY sound reads the
/// buffer while it plays. Never blocks; a new cue cuts off the one playing.
/// </summary>
internal static class SoundCues
{
    private const int Rate = 22050;
    private static readonly Lazy<nint> StartTone = new(() => Tone([660, 880]));
    private static readonly Lazy<nint> StopTone = new(() => Tone([880, 587]));

    public static void Play(SoundCue cue)
    {
        var sound = cue == SoundCue.Start ? StartTone.Value : StopTone.Value;
        NativeShell.PlaySound(sound, 0, NativeShell.SND_MEMORY | NativeShell.SND_ASYNC | NativeShell.SND_NODEFAULT);
    }

    /// <summary>A 16-bit mono WAV of 60 ms notes, faded in and out so they do not click, at a fifth of full scale.</summary>
    internal static byte[] Wav(int[] notes)
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

    private static nint Tone(int[] notes)
    {
        var wav = Wav(notes);
        var memory = Marshal.AllocHGlobal(wav.Length); // never freed: the cue can play until the process ends
        Marshal.Copy(wav, 0, memory, wav.Length);
        return memory;
    }
}
