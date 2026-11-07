using Dictum.Meetings.Speakers;

namespace Dictum.Meetings.Store;

public sealed partial class FileMeetingStore
{
    internal const string SpeakersFile = "speakers.json.enc";

    public MeetingSpeakers? ReadSpeakers(MeetingId id)
    {
        lock (Gate) return WithKey(id, key =>
        {
            var path = FileAt(id, SpeakersFile);
            return File.Exists(path) ? EncryptedRead<MeetingSpeakers>(id, path, key).Validated() : null;
        });
    }

    public void WriteSpeakers(MeetingId id, MeetingSpeakers speakers)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        speakers.Validated();
        // Inside the meeting folder only (AAD <id>/speakers.json.enc): the bundle carries it, and Delete removes it with the folder.
        lock (Gate) WithKey(id, key => { EncryptedWrite(id, FileAt(id, SpeakersFile), speakers, key); return true; });
    }
}
