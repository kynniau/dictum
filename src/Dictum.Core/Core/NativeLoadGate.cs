namespace Dictum.Core;

/// <summary>
/// One process-wide gate for building native model state: loading a whisper.cpp model (WhisperFactory.FromPath), building
/// a whisper processor, and loading a llama.cpp model. Two of these at once crashed the test process with an access
/// violation (0xC0000005 in 4 of 8 gated runs, lens review of the restructure, 16/10/2026): calibration loads models
/// outside the app's serialised model managers, and "Use this" during a calibration run starts the app's own load. Every
/// such call in Dictum goes through <see cref="Run{T}"/>, so loads queue instead of overlapping. Transcription and
/// clean-up calls are not gated: each engine already serialises its own work.
/// </summary>
public static class NativeLoadGate
{
    private static readonly object Gate = new();

    public static T Run<T>(Func<T> load)
    {
        lock (Gate) return load();
    }
}
