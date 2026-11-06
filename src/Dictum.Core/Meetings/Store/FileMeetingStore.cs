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
    private void EncryptedWrite<T>(MeetingId id, string path, T value, byte[] key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { using var input = new MemoryStream(bytes); MeetingCrypto.Encrypt(input, path, key, Identity(id, Path.GetFileName(path))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static T EncryptedRead<T>(MeetingId id, string path, byte[] key)
    {
        using var input = MeetingCrypto.OpenDecrypted(path, key, Identity(id, Path.GetFileName(path)));
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
                EncryptedWrite(meta.Id, FileAt(meta.Id, "transcript.json.enc"), new TranscriptSnapshot([], []), content);
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
            return meta with { HasVideo = meta.HasVideo || Directory.EnumerateFiles(Folder(id), "video.*.enc").Any() };
        }
    }
    private static string Identity(MeetingId id, string fileName) => $"{id.Value}/{fileName}";
    private static string AudioName(Channel channel) => channel switch
    {
        Channel.You => "audio-you.wav.enc", Channel.Others => "audio-others.wav.enc",
        Channel.Mixed => "audio-mixed.wav.enc", _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
    public void WriteAudio(MeetingId id, Channel channel, Stream wav16k)
    {
        lock (Gate) WithKey(id, key => { MeetingCrypto.Encrypt(wav16k, FileAt(id, AudioName(channel)), key, Identity(id, AudioName(channel))); return true; });
    }
    public Stream OpenAudio(MeetingId id, Channel channel)
    {
        lock (Gate) return WithKey(id, content => File.Exists(FileAt(id, ChunkName(channel)))
            ? MeetingCrypto.OpenChunkedAudio(FileAt(id, ChunkName(channel)), content, Identity(id, ChunkName(channel)), Read(id).IncompleteAudioChannels.Contains(channel))
            : MeetingCrypto.OpenDecrypted(FileAt(id, AudioName(channel)), content, Identity(id, AudioName(channel))));
    }
    private static string ChunkName(Channel channel) => AudioName(channel).Replace(".wav.enc", ".chunks.enc", StringComparison.Ordinal);
    public IMeetingAudioWriter BeginAudio(MeetingId id, Channel channel)
    {
        lock (Gate) return WithKey(id, content =>
        {
            var path = FileAt(id, ChunkName(channel));
            if (File.Exists(path) || File.Exists(FileAt(id, AudioName(channel)))) throw new IOException("Audio already exists.");
            var meta = Read(id);
            JsonWrite(FileAt(id, "meta.json"), meta with { IncompleteAudioChannels = [.. meta.IncompleteAudioChannels, channel] });
            return MeetingCrypto.BeginAudio(path, content, Identity(id, ChunkName(channel)), () =>
            {
                lock (Gate)
                {
                    var current = Read(id);
                    JsonWrite(FileAt(id, "meta.json"), current with { IncompleteAudioChannels = current.IncompleteAudioChannels.Where(c => c != channel).ToArray() });
                }
            });
        });
    }

    private sealed record TranscriptSnapshot(List<Segment> Segments, List<string> IncludedBatches);
    private string[] Batches(MeetingId id) => Directory.GetFiles(Folder(id), "transcript-batch-*.json.enc").Order(StringComparer.Ordinal).ToArray();
    private TranscriptSnapshot Transcript(MeetingId id, byte[] key)
    {
        var path = FileAt(id, "transcript.json.enc");
        var snapshot = File.Exists(path) ? EncryptedRead<TranscriptSnapshot>(id, path, key) : new TranscriptSnapshot([], []);
        var included = snapshot.IncludedBatches.ToHashSet(StringComparer.Ordinal);
        foreach (var batch in Batches(id))
            if (!included.Contains(Path.GetFileName(batch))) snapshot.Segments.AddRange(EncryptedRead<List<Segment>>(id, batch, key));
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
            // Included names retain the sequence high-water mark after batch cleanup and restart.
            var previous = EncryptedRead<TranscriptSnapshot>(id, FileAt(id, "transcript.json.enc"), key);
            var sequence = previous.IncludedBatches.Concat(Batches(id).Select(Path.GetFileName))
                .Select(name => long.Parse(name!.Split('-')[2].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0).Max();
            var batch = $"transcript-batch-{checked(sequence + 1):D19}.json.enc";
            EncryptedWrite(id, FileAt(id, batch), added, key);
            var path = FileAt(id, "transcript.json.enc");
            var now = Stopwatch.GetTimestamp();
            if (!LastSnapshot.TryGetValue(path, out var last) || Stopwatch.GetElapsedTime(last, now) >= TimeSpan.FromSeconds(1))
            {
                var all = Transcript(id, key);
                var batches = Batches(id);
                var snapshot = new TranscriptSnapshot(all.Segments, batches.Select(Path.GetFileName).Select(n => n!).ToList());
                EncryptedWrite(id, path, snapshot, key);
                LastSnapshot[path] = now;
                foreach (var file in batches) File.Delete(file);
            }
            return true;
        });
    }
    public IReadOnlyList<Segment> ReadTranscript(MeetingId id)
    {
        lock (Gate) return WithKey(id, content => Transcribe.EchoDeduplicator.Filter(Transcript(id, content).Segments));
    }
    public void AddMarker(MeetingId id, Marker marker)
    {
        lock (Gate) { var meta = Read(id); JsonWrite(FileAt(id, "meta.json"), meta with { Markers = [.. meta.Markers, marker] }); RebuildIndex(); }
    }
    public void WriteSummary(MeetingId id, Summary summary)
    {
        lock (Gate) WithKey(id, key =>
        {
            EncryptedWrite(id, FileAt(id, "summary.md.enc"), summary, key);
            JsonWrite(FileAt(id, "meta.json"), Read(id) with { SummaryEngine = summary.Engine, SummaryTemplate = summary.Template });
            RebuildIndex();
            return true;
        });
    }
    public Summary? ReadSummary(MeetingId id)
    {
        lock (Gate) return WithKey(id, key => File.Exists(FileAt(id, "summary.md.enc")) ? EncryptedRead<Summary>(id, FileAt(id, "summary.md.enc"), key) : null);
    }
    public void WriteVideo(MeetingId id, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || extension.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Invalid video extension.");
        lock (Gate) WithKey(id, key =>
        {
            MeetingCrypto.EncryptFile(sourcePath, FileAt(id, $"video.{extension}.enc"), key, Identity(id, $"video.{extension}.enc"));
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
            try { meetings.Add(Read(new MeetingId(name))); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // An interrupted delete or damaged metadata must not hide other meetings.
            }
        }
        meetings = meetings.OrderBy(m => m.StartedAt).ThenBy(m => m.Id).ToList();
        // Persist only listing/search metadata. Key wraps belong exclusively in meta.json.
        JsonWrite(Path.Combine(Root, "index.json"), meetings.Select(m => new
        {
            m.Id, m.Title, m.StartedAt, m.Duration, m.Source, m.HasVideo, m.SummaryEngine, m.SummaryTemplate, m.AudioIncomplete
        }).ToArray());
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
        ApplyRetention(now, _ => null);
    }
    internal void ApplyRetention(DateTimeOffset now, Func<MeetingId, KeepAudio?> confirmedRetention)
    {
        lock (Gate)
            foreach (var meta in List())
            {
                var keep = confirmedRetention(meta.Id) ?? meta.Agreement?.Confirmation?.Route?.KeepAudio ?? _settings.KeepAudio;
                var days = keep switch { KeepAudio.Forever => int.MaxValue, KeepAudio.Days30 => 30, KeepAudio.Days90 => 90, _ => throw new InvalidOperationException() };
                if (days != int.MaxValue && now - (meta.StartedAt + meta.Duration) >= TimeSpan.FromDays(days))
                    foreach (var audio in Directory.GetFiles(Folder(meta.Id), "audio-*.enc")) File.Delete(audio);
            }
    }
    public void Delete(MeetingId id)
    {
        lock (Gate)
        {
            var folder = Folder(id);
            if (!Path.GetFullPath(folder).StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid meeting folder.");
            // Remove crash leftovers first: metadata temp files can contain valid key wraps.
            if (Directory.Exists(folder))
                foreach (var temp in Directory.GetFiles(folder, "*.tmp"))
                {
                    Scrub(temp);
                    File.Delete(temp);
                }
            // Scrub without parsing, so deletion can resume after damaged or zero-filled metadata.
            // simplification: logical overwrites cannot erase SSD snapshots/backups; retire if a
            // platform supplies guaranteed secure erasure. Existing external copies cannot be revoked.
            if (File.Exists(FileAt(id, "meta.json")))
            {
                Scrub(FileAt(id, "meta.json"));
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            LastSnapshot.Remove(FileAt(id, "transcript.json.enc"));
            RebuildIndex();
        }
    }
    internal const int ScrubChunk = 1 << 20;
    /// <summary>Zero-fills the file in place in fixed-size chunks, so a multi-gigabyte crash leftover (a video temp) is scrubbed without one huge buffer.</summary>
    internal static void Scrub(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        var zeros = new byte[ScrubChunk];
        for (var left = stream.Length; left > 0; left -= ScrubChunk) stream.Write(zeros, 0, (int)Math.Min(left, ScrubChunk));
        stream.Flush(true);
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
