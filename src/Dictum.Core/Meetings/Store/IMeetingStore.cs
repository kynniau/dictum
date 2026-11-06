namespace Dictum.Meetings.Store;

public interface IMeetingStore
{
    MeetingId Create(MeetingMeta meta);
    void WriteAudio(MeetingId id, Channel channel, Stream wav16k);
    IMeetingAudioWriter BeginAudio(MeetingId id, Channel channel) => throw new NotSupportedException("Appendable audio is required for live recording.");
    void AppendSegments(MeetingId id, IEnumerable<Segment> segments);
    void AddMarker(MeetingId id, Marker marker);
    void WriteSummary(MeetingId id, Summary summary);
    void WriteVideo(MeetingId id, string sourcePath);
    MeetingMeta Read(MeetingId id);
    IReadOnlyList<Segment> ReadTranscript(MeetingId id);
    Summary? ReadSummary(MeetingId id);
    IReadOnlyList<Summary> ReadSummaries(MeetingId id) => ReadSummary(id) is { } summary ? [summary] : [];
    void EditSummary(MeetingId id, string summaryId, string markdown) => throw new NotSupportedException();
    void DeleteSummary(MeetingId id, string summaryId) => throw new NotSupportedException();
    string ExportMarkdown(MeetingId id, string? summaryId) => ExportMarkdown(id);
    Stream OpenAudio(MeetingId id, Channel channel);
    IReadOnlyList<MeetingMeta> List();
    IReadOnlyList<(MeetingMeta, string Snippet)> Search(string text);
    void Delete(MeetingId id);
    long SizeOf(MeetingId id);
    void ApplyRetention(DateTimeOffset now);
    string ExportMarkdown(MeetingId id);
    void ExportBundle(MeetingId id, string destinationPath, string transferCode) => throw new NotSupportedException();
}

public interface IMeetingAudioWriter : IDisposable
{
    void Append(float[] samples);
    void Complete();
}
