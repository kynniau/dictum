using Dictum.Audio;

namespace Dictum.Meetings.Transcribe;

public interface IMeetingAudioSource
{
    IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(CancellationToken cancellationToken);

    /// <summary>No future part starts before this point. Unordered sources may leave the default until EOF.</summary>
    TimeSpan Watermark => TimeSpan.MinValue;
}

public interface IMeetingTranscriber
{
    Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken);
}
