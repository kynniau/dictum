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
    /// <summary>
    /// The meeting's speaker separation (speakers.json.enc), or null when none has run, and null for a file this version cannot
    /// accept after it decrypts (design 1.6 B). A file that fails decryption throws.
    /// </summary>
    Speakers.MeetingSpeakers? ReadSpeakers(MeetingId id) => null;
    /// <summary>Whether the meeting has a speakers file, readable by this version or not.</summary>
    bool HasSpeakers(MeetingId id) => ReadSpeakers(id) is not null;
    /// <summary>Replaces the meeting's speaker separation and names; only ever inside the meeting folder, sealed like its other files.</summary>
    void WriteSpeakers(MeetingId id, Speakers.MeetingSpeakers speakers) => throw new NotSupportedException();
}

public interface IMeetingAudioWriter : IDisposable
{
    void Append(float[] samples);
    void Complete();
}
