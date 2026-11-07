using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Meetings.Speakers;

namespace Dictum.Meetings.Store;

public sealed partial class FileMeetingStore
{
    internal const string SpeakersFile = "speakers.json.enc";

    /// <summary>
    /// The meeting's speakers, or null for none, and null for a file this version cannot accept once it has decrypted and
    /// authenticated (a newer version from a bundle, or content <see cref="MeetingSpeakers.Validated"/> refuses): the meeting then
    /// summarises, exports and separates again with plain channel labels. A file that fails decryption or authentication still
    /// throws, so tampering stays loud. The whole file is authenticated before any of it is parsed.
    /// </summary>
    public MeetingSpeakers? ReadSpeakers(MeetingId id)
    {
        lock (Gate) return WithKey(id, key =>
        {
            var path = FileAt(id, SpeakersFile);
            if (!File.Exists(path)) return null;
            byte[] plain;
            using (var input = MeetingCrypto.OpenDecrypted(path, key, Identity(id, SpeakersFile)))
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer); // throws CryptographicException on a failed tag before anything is parsed
                plain = buffer.ToArray();
                CryptographicOperations.ZeroMemory(buffer.GetBuffer());
            }
            try { return JsonSerializer.Deserialize<MeetingSpeakers>(plain)?.Validated(); }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException) { return null; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        });
    }

    /// <summary>Whether the meeting has a speakers file, readable by this version or not (Delete removes it either way).</summary>
    public bool HasSpeakers(MeetingId id)
    {
        lock (Gate) return File.Exists(FileAt(id, SpeakersFile));
    }

    public void WriteSpeakers(MeetingId id, MeetingSpeakers speakers)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        speakers.Validated();
        // Inside the meeting folder only (AAD <id>/speakers.json.enc): the bundle carries it, and Delete removes it with the folder.
        lock (Gate) WithKey(id, key => { EncryptedWrite(id, FileAt(id, SpeakersFile), speakers, key); return true; });
    }
}
