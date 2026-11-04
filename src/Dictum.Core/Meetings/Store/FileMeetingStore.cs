using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dictum.Settings;

namespace Dictum.Meetings.Store;

public sealed class FileMeetingStore : IMeetingStore
{
    // simplification: serialize file operations in this process; retire for concurrent multi-process
    // writers (team-folder phase) with an interprocess lock and conflict resolution.
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMeetingKeyring _keyring;
    private readonly MeetingsSettings _settings;
    private readonly Func<string, string> _captionFor;
    public string Root { get; }

    public FileMeetingStore(AppPaths paths, MeetingsSettings settings, IMeetingKeyring keyring, Func<string, string> captionFor)
        : this(settings.Folder ?? Path.Combine(paths.LocalDir, "meetings"), settings, keyring, captionFor) { }

    public FileMeetingStore(string root, MeetingsSettings settings, IMeetingKeyring keyring, Func<string, string> captionFor)
    {
        Root = Path.GetFullPath(settings.Folder ?? root);
        (_settings, _keyring, _captionFor) = (settings, keyring, captionFor);
    }

    private string Folder(MeetingId id)
    {
        // Revalidate default(struct) too, before forming any path.
        _ = new MeetingId(id.Value);
        return Path.Combine(Root, id.Value);
    }
    private string FileAt(MeetingId id, string name) => Path.Combine(Folder(id), name);
    private static void JsonWrite<T>(string path, T value) => AtomicFile.Write(path, s => JsonSerializer.Serialize(s, value));
    private static T JsonRead<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid meeting data.");
    private void EncryptedWrite<T>(string path, T value, byte[] key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { using var input = new MemoryStream(bytes); MeetingCrypto.Encrypt(input, path, key); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static T EncryptedRead<T>(string path, byte[] key)
    {
        using var input = MeetingCrypto.OpenDecrypted(path, key);
        return JsonSerializer.Deserialize<T>(input) ?? throw new InvalidDataException("Invalid meeting data.");
    }
    private T WithKey<T>(MeetingId id, Func<byte[], T> action)
    {
        var owner = _keyring.OwnerKey();
        byte[]? key = null;
        try
        {
            var wraps = Read(id).Wraps;
            if (wraps.Count != 1 || wraps[0].RecipientId != "owner") throw new CryptographicException("Invalid owner wrap.");
            key = MeetingCrypto.Unwrap(wraps[0], owner);
            return action(key);
        }
        finally { CryptographicOperations.ZeroMemory(owner); if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    public MeetingId Create(MeetingMeta meta)
    {
        lock (Gate)
        {
            var owner = _keyring.OwnerKey(); // Must succeed before creating even the root.
            var content = MeetingCrypto.NewContentKey();
            try
            {
                var folder = Folder(meta.Id);
                if (Directory.Exists(folder)) throw new IOException("Meeting already exists.");
                var wrap = MeetingCrypto.Wrap(content, owner, "owner");
                Directory.CreateDirectory(folder);
                JsonWrite(FileAt(meta.Id, "meta.json"), meta with { Wraps = [wrap] });
                EncryptedWrite(FileAt(meta.Id, "transcript.json.enc"), new TranscriptSnapshot([], []), content);
                RebuildIndex();
                return meta.Id;
            }
            finally { CryptographicOperations.ZeroMemory(owner); CryptographicOperations.ZeroMemory(content); }
        }
    }

    public MeetingMeta Read(MeetingId id)
    {
        lock (Gate)
        {
            var meta = JsonRead<MeetingMeta>(FileAt(id, "meta.json"));
            if (meta.Id != id) throw new InvalidDataException("Meeting id mismatch.");
            return meta;
        }
    }
    private static string AudioName(Channel channel) => channel switch
    {
        Channel.You => "audio-you.wav.enc", Channel.Others => "audio-others.wav.enc",
        Channel.Mixed => "audio-mixed.wav.enc", _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
    public void WriteAudio(MeetingId id, Channel channel, Stream wav16k)
    {
        lock (Gate) WithKey(id, key => { MeetingCrypto.Encrypt(wav16k, FileAt(id, AudioName(channel)), key); return true; });
    }
    public Stream OpenAudio(MeetingId id, Channel channel)
    {
        lock (Gate) return WithKey(id, key => MeetingCrypto.OpenDecrypted(FileAt(id, AudioName(channel)), key));
    }

    private sealed record TranscriptSnapshot(List<Segment> Segments, List<string> IncludedBatches);
    private string[] Batches(MeetingId id) => Directory.GetFiles(Folder(id), "transcript-batch-*.json.enc").Order(StringComparer.Ordinal).ToArray();
    private TranscriptSnapshot Transcript(MeetingId id, byte[] key)
    {
        var snapshot = EncryptedRead<TranscriptSnapshot>(FileAt(id, "transcript.json.enc"), key);
        var included = snapshot.IncludedBatches.ToHashSet(StringComparer.Ordinal);
        foreach (var batch in Batches(id))
            if (!included.Contains(Path.GetFileName(batch))) snapshot.Segments.AddRange(EncryptedRead<List<Segment>>(batch, key));
        return snapshot;
    }
    public void AppendSegments(MeetingId id, IEnumerable<Segment> segments)
    {
        lock (Gate) WithKey(id, key =>
        {
            var added = segments.ToList();
            if (added.Count == 0) return true;
            // Durable encrypted batches avoid buffering uncommitted transcript in memory. A snapshot
            // records incorporated batch names before cleanup, making interruption of cleanup harmless.
            var batch = $"transcript-batch-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json.enc";
            EncryptedWrite(FileAt(id, batch), added, key);
            var path = FileAt(id, "transcript.json.enc");
            var now = Stopwatch.GetTimestamp();
            if (!LastSnapshot.TryGetValue(path, out var last) || Stopwatch.GetElapsedTime(last, now) >= TimeSpan.FromSeconds(1))
            {
                var all = Transcript(id, key);
                var batches = Batches(id);
                var snapshot = new TranscriptSnapshot(all.Segments, batches.Select(Path.GetFileName).Select(n => n!).ToList());
                EncryptedWrite(path, snapshot, key);
                LastSnapshot[path] = now;
                foreach (var file in batches) File.Delete(file);
            }
            return true;
        });
    }
    public IReadOnlyList<Segment> ReadTranscript(MeetingId id)
    {
        lock (Gate) return WithKey(id, key => (IReadOnlyList<Segment>)Transcript(id, key).Segments);
    }
    public void AddMarker(MeetingId id, Marker marker)
    {
        lock (Gate) { var meta = Read(id); JsonWrite(FileAt(id, "meta.json"), meta with { Markers = [.. meta.Markers, marker] }); RebuildIndex(); }
    }
    public void WriteSummary(MeetingId id, Summary summary)
    {
        lock (Gate) WithKey(id, key =>
        {
            EncryptedWrite(FileAt(id, "summary.md.enc"), summary, key);
            JsonWrite(FileAt(id, "meta.json"), Read(id) with { SummaryEngine = summary.Engine, SummaryTemplate = summary.Template });
            RebuildIndex();
            return true;
        });
    }
    public Summary? ReadSummary(MeetingId id)
    {
        lock (Gate) return WithKey(id, key => File.Exists(FileAt(id, "summary.md.enc")) ? EncryptedRead<Summary>(FileAt(id, "summary.md.enc"), key) : null);
    }
    public void WriteVideo(MeetingId id, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || extension.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Invalid video extension.");
        lock (Gate) WithKey(id, key =>
        {
            MeetingCrypto.EncryptFile(sourcePath, FileAt(id, $"video.{extension}.enc"), key);
            JsonWrite(FileAt(id, "meta.json"), Read(id) with { HasVideo = true });
            RebuildIndex();
            return true;
        });
    }
    private List<MeetingMeta> RebuildIndex()
    {
        if (!Directory.Exists(Root)) return [];
        var meetings = new List<MeetingMeta>();
        foreach (var directory in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 32 || name.Any(c => !char.IsAsciiHexDigitLower(c))) continue;
            if (File.Exists(Path.Combine(directory, "meta.json"))) meetings.Add(Read(new MeetingId(name)));
        }
        meetings = meetings.OrderBy(m => m.StartedAt).ThenBy(m => m.Id).ToList();
        JsonWrite(Path.Combine(Root, "index.json"), meetings);
        return meetings;
    }
    public IReadOnlyList<MeetingMeta> List()
    {
        // simplification: rebuild on every listing to detect stale indexes without filesystem timestamp
        // assumptions; retire with a journal/index when there are thousands of meetings or sub-second search is required.
        lock (Gate) return RebuildIndex();
    }
    public IReadOnlyList<(MeetingMeta, string Snippet)> Search(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (Gate)
        {
            var found = new List<(MeetingMeta, string)>();
            foreach (var meta in List())
            {
                var transcript = string.Join("\n", ReadTranscript(meta.Id).Select(s => s.Text));
                var at = transcript.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var start = Math.Max(0, at - 40);
                found.Add((meta, transcript.Substring(start, Math.Min(transcript.Length - start, Math.Max(120, text.Length + 40)))));
            }
            return found;
        }
    }
    public long SizeOf(MeetingId id)
    {
        lock (Gate) return Directory.GetFiles(Folder(id), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }
    public void ApplyRetention(DateTimeOffset now)
    {
        var days = _settings.KeepAudio switch { KeepAudio.Forever => int.MaxValue, KeepAudio.Days30 => 30, KeepAudio.Days90 => 90, _ => throw new InvalidOperationException() };
        if (days == int.MaxValue) return;
        lock (Gate)
            foreach (var meta in List().Where(m => now - (m.StartedAt + m.Duration) >= TimeSpan.FromDays(days)))
                foreach (var audio in Directory.GetFiles(Folder(meta.Id), "audio-*.wav.enc")) File.Delete(audio);
    }
    public void Delete(MeetingId id)
    {
        lock (Gate)
        {
            var meta = Read(id);
            var scrubbed = meta with { Wraps = meta.Wraps.Select(w => new KeyWrap(w.RecipientId, new byte[w.WrappedKey.Length])).ToArray() };
            // Overwrite the original key material in place before the atomic metadata replacement.
            // simplification: logical overwrites cannot erase SSD snapshots/backups; retire if a
            // platform supplies guaranteed secure erasure. Existing external copies cannot be revoked.
            using (var stream = new FileStream(FileAt(id, "meta.json"), FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[checked((int)stream.Length)];
                stream.Write(zeros); stream.Flush(true);
            }
            JsonWrite(FileAt(id, "meta.json"), scrubbed);
            var folder = Folder(id);
            if (!Path.GetFullPath(folder).StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid meeting folder.");
            Directory.Delete(folder, true);
            LastSnapshot.Remove(FileAt(id, "transcript.json.enc"));
            RebuildIndex();
        }
    }
    public string ExportMarkdown(MeetingId id)
    {
        lock (Gate)
        {
            var meta = Read(id);
            var summary = ReadSummary(id);
            var result = new StringBuilder().Append("# ").AppendLine(meta.Title).AppendLine();
            if (summary is not null) result.AppendLine(summary.Markdown).AppendLine().AppendLine(_captionFor(summary.Engine)).AppendLine();
            foreach (var segment in ReadTranscript(id)) result.Append('[').Append(segment.Start).Append("] ").Append(segment.Channel).Append(": ").AppendLine(segment.Text);
            return result.ToString();
        }
    }
}
