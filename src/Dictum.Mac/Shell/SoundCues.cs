using Dictum.Audio;
using Dictum.Mac.Interop;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The start and stop sounds (sounds.enabled) on macOS: the same two tones the Windows build plays (<see cref="CueTones"/>),
/// each an NSSound made once from the WAV bytes (NSData <c>dataWithBytes:length:</c> copies them; NSSound.h:40
/// <c>initWithData:</c>) and kept for the life of the process. <c>play</c> is asynchronous (NSSound.h:52, "sound is played
/// asynchronously"), so this never blocks; a new cue stops the one playing first (<c>stop</c>, :55), as on Windows. The
/// UI calls it on its thread (AppShell.SetTrayState).
/// </summary>
internal static class SoundCues
{
    private static readonly Lazy<nint> StartSound = new(() => Make(SoundCue.Start));
    private static readonly Lazy<nint> StopSound = new(() => Make(SoundCue.Stop));

    public static void Play(SoundCue cue)
    {
        var sound = Sound(cue);
        if (sound == 0) return;
        using var pool = ObjC.AutoreleasePool.Begin();
        AppKit.SendBool(sound, ObjC.Sel("stop"));
        AppKit.SendBool(sound, ObjC.Sel("play"));
    }

    /// <summary>The cue's NSSound (owned, never released), or 0 when macOS could not read the WAV.</summary>
    internal static nint Sound(SoundCue cue) => cue == SoundCue.Start ? StartSound.Value : StopSound.Value;

    private static unsafe nint Make(SoundCue cue)
    {
        var wav = CueTones.Wav(CueTones.Notes(cue));
        using var pool = ObjC.AutoreleasePool.Begin();
        nint data;
        fixed (byte* bytes = wav) data = ObjC.Send(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), (nint)bytes, wav.Length);
        return ObjC.Send(ObjC.Send(ObjC.Class("NSSound"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
    }
}
