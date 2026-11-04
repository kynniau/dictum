using System.Diagnostics;

namespace Dictum.Audio;

public enum CaptureActivity { Speech, Idle, Silent }

/// <summary>An atomic snapshot. No future part (including the stop tail) may start before PendingStart.
/// Idle/silent captures must flush quiet audio before advancing PendingStart; speech holds it at the buffered part's start.</summary>
public readonly record struct CaptureProgress(CaptureActivity Activity, TimeSpan PendingStart);

/// <summary>Optional meeting capture protocol. Callbacks must not block. All offsets use the supplied running session clock.</summary>
public interface ITimestampedCapture : IAudioCapture
{
    void Start(string? deviceSetting, Stopwatch sessionClock);
    void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason, TimeSpan> part);
    CaptureProgress Progress { get; }
    /// <summary>Stops and returns the tail with its exact start, even when it is empty.</summary>
    (CapturedAudio Part, TimeSpan Offset) StopTimestamped();
}
