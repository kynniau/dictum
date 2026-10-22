using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Platform;
using Dictum.Windows.Interop;

namespace Dictum.Windows.Shell;

/// <summary>
/// The start and stop sounds (sounds.enabled): the two tones <see cref="CueTones"/> makes in memory (no sound files ship),
/// played with winmm's PlaySound from unmanaged memory that lives as long as the process, because an asynchronous
/// SND_MEMORY sound reads the buffer while it plays. Never blocks; a new cue cuts off the one playing.
/// </summary>
internal static class SoundCues
{
    private static readonly Lazy<nint> StartTone = new(() => Tone(SoundCue.Start));
    private static readonly Lazy<nint> StopTone = new(() => Tone(SoundCue.Stop));

    public static void Play(SoundCue cue)
    {
        var sound = cue == SoundCue.Start ? StartTone.Value : StopTone.Value;
        NativeShell.PlaySound(sound, 0, NativeShell.SND_MEMORY | NativeShell.SND_ASYNC | NativeShell.SND_NODEFAULT);
    }

    private static nint Tone(SoundCue cue)
    {
        var wav = CueTones.Wav(CueTones.Notes(cue));
        var memory = Marshal.AllocHGlobal(wav.Length); // never freed: the cue can play until the process ends
        Marshal.Copy(wav, 0, memory, wav.Length);
        return memory;
    }
}
