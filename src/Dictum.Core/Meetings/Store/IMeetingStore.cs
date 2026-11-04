namespace Dictum.Meetings.Store;

public interface IMeetingStore
{
    MeetingId Create(MeetingMeta meta);
    void WriteAudio(MeetingId id, Channel channel, Stream wav16k);
    void AppendSegments(MeetingId id, IEnumerable<Segment> segments);
    void AddMarker(MeetingId id, Marker marker);
    void WriteSummary(MeetingId id, Summary summary);
    void WriteVideo(MeetingId id, string sourcePath);
    MeetingMeta Read(MeetingId id);
    IReadOnlyList<Segment> ReadTranscript(MeetingId id);
    Summary? ReadSummary(MeetingId id);
    Stream OpenAudio(MeetingId id, Channel channel);
    IReadOnlyList<MeetingMeta> List();
    IReadOnlyList<(MeetingMeta, string Snippet)> Search(string text);
    void Delete(MeetingId id);
    long SizeOf(MeetingId id);
    void ApplyRetention(DateTimeOffset now);
    string ExportMarkdown(MeetingId id);
}
