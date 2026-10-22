// PLACEHOLDER: replaced by the audio lane (docs/MAC-HANDOVER.md §5). Nothing here ships.
using Dictum.Audio;

namespace Dictum.Mac.Audio;

internal sealed class AudioQueueCapture(ILog log) : IAudioCapture, IDisposable
{
    public float CurrentLevel => 0f;
    public event Action<Exception>? Failed;
    public void Start(string? deviceSetting, long keyDownStamp) => throw new NotImplementedException("audio lane");
    public CapturedAudio Stop() => throw new NotImplementedException("audio lane");
    public void Dispose() { }
}

internal static class CaptureDevices
{
    public static IReadOnlyList<AudioDeviceInfo> List() => throw new NotImplementedException("audio lane");
}
