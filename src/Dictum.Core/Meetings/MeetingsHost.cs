using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Meetings.Import;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Meetings.Transcribe;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Meetings;

internal sealed record MeetingsHost(
    AppPaths Paths, Func<MeetingsSettings> Settings, IMeetingStore Store,
    Func<string, Agreement, CancellationToken, Task<MeetingId>> Import,
    IMeetingTranscriber Transcriber, Func<IAudioCapture> Capture,
    Func<SummaryEngine, CancellationToken, Task<ILlmClient>> SummaryClient,
    Action<string> Copy, Action<MeetingId, TimeSpan> Finish,
    Func<string, Task<bool?>> DriveEncrypted, Func<bool> WarningDismissed, Action DismissWarning)
{
    public const string PROPOSED_UnrecordedProvider = "the service used for this summary (provider not recorded)";
    public const string PROPOSED_ExportReminder = "Sharing a recording or transcript may need the other people's agreement.";
    public string Export(MeetingId id) => Store.ExportMarkdown(id) + Environment.NewLine + PROPOSED_ExportReminder + Environment.NewLine;
    public static string Caption(string storedEngine)
    {
        // Summary has no provider field. New own-key summaries retain the client identity in Engine,
        // so later configuration changes cannot change the attribution on screen or in exports.
        const string prefix = "OwnKey:";
        return storedEngine.StartsWith(prefix, StringComparison.Ordinal)
            ? SummaryCaption.For("OwnKey", storedEngine[prefix.Length..])
            : storedEngine == "OwnKey" ? SummaryCaption.For("OwnKey", PROPOSED_UnrecordedProvider)
            : SummaryCaption.For(storedEngine);
    }
    public ILog Log { get; init; } = NullLog.Instance;
    public Func<string?> MicrophoneDevice { get; init; } = () => null;
    public Func<double> SilenceThreshold { get; init; } = () => 0.01;
    public Func<string?> CloudSpeechHost { get; init; } = () => null;
    public Func<Task<string?>>? ChooseImport { get; init; }
    public Func<IAudioCapture?> LoopbackCapture { get; init; } = () => null;
    public Func<bool> ImportsEnabled { get; init; } = () => false;
    public Action EnableImports { get; init; } = () => { };
    public Func<string?> SummaryHost { get; init; } = () => null;
    public Action<MeetingId, MeetingConfirmation> SaveConfirmation { get; init; } = (_, _) => throw new InvalidOperationException("Confirmation storage is required.");
    // simplification: team sharing and team-folder access/logging do not exist; keep them unavailable.
    // simplification: there is no audio-off setting yet; always use the saved-audio retention notices.
    // simplification: Ollama model forwarding is not reliably detectable; show the approved conditional notice.
    public static MeetingsHost Create(AppPaths paths, Func<MeetingsSettings> settings, ICredentialStore credentials,
        IMeetingTranscriberFactory factory, Func<IAudioCapture> capture,
        Func<SummaryEngine, CancellationToken, Task<ILlmClient>> summaryClient, Action<string> copy,
        IAudioDecoder decoder)
    {
        var snapshot = settings();
        var root = snapshot.Folder ?? Path.Combine(paths.LocalDir, "meetings");
        var store = new CurrentSettingsStore(root, settings, new MeetingKeyring(credentials));
        var transcriber = factory.Create(store);
        var importer = new MeetingImporter(store, transcriber, decoder);
        var computer = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)));
        const string dismissal = "DICTUM_MEETINGS_DRIVE_WARNING";
        return new(paths, settings, store,
            (path, agreement, cancellation) => importer.ImportAsync(path, MeetingSource.ImportedFile, agreement, cancellationToken: cancellation),
            transcriber, capture, summaryClient, copy,
            (id, duration) =>
            {
                // The store contract has no metadata update. Keep its envelope intact when finalising the live duration.
                var meta = store.Read(id) with { Duration = duration };
                AtomicFile.Write(Path.Combine(root, id.Value, "meta.json"),
                    stream => JsonSerializer.Serialize(stream, meta));
            }, BitLockerStatus,
            () => credentials.Read(dismissal, computer) == "dismissed",
            () => credentials.Write(dismissal, computer, "dismissed"))
        {
            ImportsEnabled = () => credentials.Read("DICTUM_MEETINGS_IMPORTS", "owner") == "approved-2026-11-05",
            EnableImports = () => credentials.Write("DICTUM_MEETINGS_IMPORTS", "owner", "approved-2026-11-05"),
            SaveConfirmation = store.WriteConfirmation,
        };
    }

    // The folder stays pinned until reopening, as described in Settings. Read other settings per operation.
    internal sealed class CurrentSettingsStore(string root, Func<MeetingsSettings> settings, IMeetingKeyring ring) : IMeetingStore
    {
        private readonly object _confirmationGate = new();
        public void WriteConfirmation(MeetingId id, MeetingConfirmation confirmation)
        {
            lock (_confirmationGate)
            {
                var records = ReadConfirmations(id).Append(confirmation).ToArray();
                WithContent(id, content =>
                {
                    using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(records));
                    MeetingCrypto.Encrypt(input, ConfirmationPath(id), content, id.Value + "/confirmations.json.enc");
                    return true;
                });
            }
        }
        public IReadOnlyList<MeetingConfirmation> ReadConfirmations(MeetingId id) => WithContent(id, content =>
        {
            var path = ConfirmationPath(id);
            if (!File.Exists(path)) return (IReadOnlyList<MeetingConfirmation>)Array.Empty<MeetingConfirmation>();
            using var input = MeetingCrypto.OpenDecrypted(path, content, id.Value + "/confirmations.json.enc");
            return JsonSerializer.Deserialize<MeetingConfirmation[]>(input) ?? throw new InvalidDataException();
        });
        private string ConfirmationPath(MeetingId id) => Path.Combine(root, new MeetingId(id.Value).Value, "confirmations.json.enc");
        private T WithContent<T>(MeetingId id, Func<byte[], T> action)
        {
            var owner = ring.OwnerKey();
            byte[]? content = null;
            try
            {
                var wrap = Current().Read(id).Wraps.Single(w => w.RecipientId == "owner");
                content = MeetingCrypto.Unwrap(wrap, owner);
                return action(content);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(owner);
                if (content is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
            }
        }
        private FileMeetingStore Current() => new(root, settings() with { Folder = root }, ring, Caption);
        public MeetingId Create(MeetingMeta meta) => Current().Create(meta);
        public void WriteAudio(MeetingId id, Channel channel, Stream audio) => Current().WriteAudio(id, channel, audio);
        public void AppendSegments(MeetingId id, IEnumerable<Segment> segments) => Current().AppendSegments(id, segments);
        public void AddMarker(MeetingId id, Marker marker) => Current().AddMarker(id, marker);
        public void WriteSummary(MeetingId id, Summary summary) => Current().WriteSummary(id, summary);
        public void WriteVideo(MeetingId id, string path) => Current().WriteVideo(id, path);
        public MeetingMeta Read(MeetingId id) => Current().Read(id);
        public IReadOnlyList<Segment> ReadTranscript(MeetingId id) => Current().ReadTranscript(id);
        public Summary? ReadSummary(MeetingId id) => Current().ReadSummary(id);
        public Stream OpenAudio(MeetingId id, Channel channel) => Current().OpenAudio(id, channel);
        public IReadOnlyList<MeetingMeta> List() => Current().List();
        public IReadOnlyList<(MeetingMeta, string Snippet)> Search(string text) => Current().Search(text);
        public void Delete(MeetingId id) => Current().Delete(id);
        public long SizeOf(MeetingId id) => Current().SizeOf(id);
        public void ApplyRetention(DateTimeOffset now) => Current().ApplyRetention(now);
        public string ExportMarkdown(MeetingId id) => Current().ExportMarkdown(id);
    }

    public async Task Regenerate(MeetingId id, string template, CancellationToken cancellation)
    {
        var engine = Settings().SummaryEngine;
        var client = await SummaryClient(engine, cancellation);
        try
        {
            var summary = await new MeetingSummariser().SummariseAsync(id, Store.ReadTranscript(id),
                SummaryTemplates.Get(template, Paths.ConfigDir), client,
                engine == SummaryEngine.OwnKey ? "OwnKey:" + client.Name : engine.ToString(), cancellation);
            Store.WriteSummary(id, summary);
        }
        finally { (client as IDisposable)?.Dispose(); }
    }

    private static async Task<bool?> BitLockerStatus(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // simplification: a non-admin process may not read BitLocker reliably; unknown is never treated as off.
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(folder));
            if (drive is null || drive.Length != 3 || drive[1] != ':') return null;
            // A mounted folder or redirected ancestor may belong to a different volume from its drive letter.
            for (var ancestor = new DirectoryInfo(Path.GetFullPath(folder)); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"$ErrorActionPreference='Stop'; (Get-BitLockerVolume -MountPoint '{drive[..2]}').VolumeStatus.ToString()");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(true); return null; }
            var status = (await output).Trim();
            if (process.ExitCode != 0 || (await errors).Length != 0) return null;
            return status switch { "FullyDecrypted" => false, "FullyEncrypted" => true, _ => null };
        }
        catch { return null; }
    }
}

internal interface IMeetingTranscriberFactory
{
    IMeetingTranscriber Create(IMeetingStore store);
}

internal sealed class MeetingTranscriberFactory(Func<CancellationToken, Task<ITranscriber>> speech,
    Func<IVoiceGate?> gate, Func<double> silenceThreshold, Dictum.Personal.PersonalDictionary dictionary) : IMeetingTranscriberFactory
{
    public IMeetingTranscriber Create(IMeetingStore store) => new MeetingTranscriber(new SpeechAdapter(speech),
        new GateAdapter(gate, silenceThreshold), store, dictionary);
    private sealed class SpeechAdapter(Func<CancellationToken, Task<ITranscriber>> speech) : ISpeechEngine
    {
        public string ModelPath => "";
        public DecodeOptions Decode => DecodeOptions.Default;
        public void Reconfigure(DecodeOptions decode) { }
        public void Dispose() { }
        public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken) =>
            await (await speech(cancellationToken)).TranscribeAsync(samples16kMono, cancellationToken);
        public async Task<string> TranscribeAsync(float[] samples16kMono, string? carried, CancellationToken cancellationToken) =>
            await (await speech(cancellationToken)).TranscribeAsync(samples16kMono, carried, cancellationToken);
    }
    private sealed class GateAdapter(Func<IVoiceGate?> gate, Func<double> threshold) : IVoiceGate
    {
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            if (gate() is { } current) return current.CheckAsync(samples16kMono, cancellationToken);
            var peak = samples16kMono.Chunk(320).Select(block => Math.Sqrt(block.Average(sample => (double)sample * sample))).DefaultIfEmpty(0).Max();
            var rejected = RecordingGate.RejectReason(new CapturedAudio(samples16kMono, (float)peak), 0, threshold());
            return Task.FromResult(new VoiceVerdict(rejected is null, rejected ?? ""));
        }
    }
}

/// <summary>One microphone, with capture closed while paused. All offsets use one elapsed clock.</summary>
internal sealed class MicrophoneMeetingSource(IAudioCapture capture, string? device, double threshold) : IHostMeetingSource
{
    private readonly Stopwatch _clock = new();
    private readonly object _sync = new();
    private readonly System.Threading.Channels.Channel<(Channel, CapturedAudio, TimeSpan)> _parts = System.Threading.Channels.Channel.CreateUnbounded<(Channel, CapturedAudio, TimeSpan)>();
    // simplification: hold microphone PCM in memory until stop because WriteAudio replaces a track;
    // retire when the store supports an appendable encrypted audio stream.
    private readonly List<float> _audio = [];
    private bool _active, _stopped;
    public TimeSpan Elapsed => _clock.Elapsed;
    public float Level => _active ? capture.CurrentLevel : 0;
    public TimeSpan Watermark => TimeSpan.MaxValue;
    public event Action<Exception>? Failed;

    public void Start()
    {
        capture.Failed += OnFailure;
        _clock.Start();
        Resume();
    }
    private void OnFailure(Exception error) => Failed?.Invoke(error);
    public void Resume()
    {
        if (_stopped || _active) return;
        _clock.Start();
        capture.Start(device, Stopwatch.GetTimestamp());
        _active = true;
        capture.SegmentAtPauses(threshold, (part, _) => Publish(part));
    }
    private void Publish(CapturedAudio part)
    {
        if (part.Samples.Length == 0) return;
        lock (_sync)
        {
            var offset = Elapsed - part.Duration;
            if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
            var required = checked((int)(offset.TotalSeconds * 16000));
            while (_audio.Count < required) _audio.Add(0);
            _audio.AddRange(part.Samples);
            _parts.Writer.TryWrite((Channel.You, part, offset));
        }
    }
    public void Pause()
    {
        if (!_active) return;
        var tail = capture.Stop();
        _clock.Stop();
        _active = false;
        Publish(tail);
    }
    public void Stop()
    {
        if (_stopped) return;
        try { Pause(); }
        finally { _stopped = true; _clock.Stop(); _parts.Writer.TryComplete(); }
    }
    public IReadOnlyList<Channel> Tracks => [Channel.You];
    public Stream Audio(Channel channel = Channel.You)
    {
        lock (_sync) return new MemoryStream(Wav.Encode(_audio.ToArray()), writable: false);
    }
    public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var part in _parts.Reader.ReadAllAsync(cancellationToken)) yield return part;
    }
    public void Dispose()
    {
        try { Stop(); }
        finally { capture.Failed -= OnFailure; (capture as IDisposable)?.Dispose(); }
    }
}

internal sealed record MeetingConfirmation(string Answer, DateTimeOffset At, AgreementKind Kind,
    string ScriptVersion, string Script, string? ImporterIdentity = null);

internal interface IHostMeetingSource : IMeetingAudioSource, IDisposable
{
    TimeSpan Elapsed { get; }
    float Level { get; }
    event Action<Exception>? Failed;
    IReadOnlyList<Channel> Tracks { get; }
    void Start();
    void Pause();
    void Resume();
    void Stop();
    Stream Audio(Channel channel = Channel.You);
}

/// <summary>Retains two tracks in memory; each resumed interval uses L2's shared-clock source.</summary>
internal sealed class TwoTrackMeetingSource(IAudioCapture microphone, IAudioCapture system,
    string? device, double threshold) : IHostMeetingSource
{
    private readonly Stopwatch _elapsed = new();
    private readonly object _sync = new();
    private readonly List<float>[] _audio = [[], []];
    private readonly System.Threading.Channels.Channel<(Channel Channel, CapturedAudio Part, TimeSpan Offset, TimeSpan Watermark)> _parts =
        System.Threading.Channels.Channel.CreateUnbounded<(Channel, CapturedAudio, TimeSpan, TimeSpan)>();
    private readonly List<Task> _pumps = [];
    private LiveTwoTrackSource? _current;
    private TimeSpan _intervalStart;
    private TimeSpan _watermark = TimeSpan.MinValue;
    private bool _stopped;
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public float Level => _current is null ? 0 : Math.Max(microphone.CurrentLevel, system.CurrentLevel);
    // Advance with the outer reader: the L2 pump can be ahead of transcription.
    public TimeSpan Watermark => _watermark;
    public IReadOnlyList<Channel> Tracks => [Channel.You, Channel.Others];
    public event Action<Exception>? Failed;
    public void Start() => Resume();
    public void Resume()
    {
        if (_stopped || _current is not null) return;
        _intervalStart = Elapsed;
        _elapsed.Start();
        var source = new LiveTwoTrackSource(microphone, system, threshold, device);
        _current = source;
        _pumps.Add(Pump(source, _intervalStart));
    }
    private async Task Pump(LiveTwoTrackSource source, TimeSpan start)
    {
        try
        {
            await foreach (var part in source.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (part.Part.Samples.Length == 0) { _parts.Writer.TryWrite((part.Channel, part.Part, start + part.Offset, start + source.Watermark)); continue; }
                var offset = start + part.Offset;
                lock (_sync)
                {
                    var track = _audio[part.Channel == Channel.You ? 0 : 1];
                    var required = checked((int)(offset.TotalSeconds * 16000));
                    while (track.Count < required + part.Part.Samples.Length) track.Add(0);
                    for (var index = 0; index < part.Part.Samples.Length; index++) track[required + index] = part.Part.Samples[index];
                }
                _parts.Writer.TryWrite((part.Channel, part.Part, offset, start + source.Watermark));
            }
        }
        catch (Exception error) { Failed?.Invoke(error); }
    }
    public void Pause()
    {
        var current = _current;
        if (current is null) return;
        current.Stop();
        // Drain this interval's capture tails before starting another shared clock.
        _pumps[^1].GetAwaiter().GetResult();
        _elapsed.Stop();
        _current = null;
        _parts.Writer.TryWrite((Channel.Mixed, CapturedAudio.Empty, Elapsed, Elapsed));
    }
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Pause();
        _ = Complete();
    }
    private async Task Complete()
    {
        await Task.WhenAll(_pumps).ConfigureAwait(false);
        _parts.Writer.TryComplete();
    }
    public Stream Audio(Channel channel = Channel.You)
    {
        lock (_sync) return new MemoryStream(Wav.Encode(_audio[channel == Channel.You ? 0 : 1].ToArray()), false);
    }
    public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var part in _parts.Reader.ReadAllAsync(cancellationToken))
        {
            _watermark = part.Watermark;
            yield return (part.Channel, part.Part, part.Offset);
        }
    }
    public void Dispose()
    {
        Stop();
        (microphone as IDisposable)?.Dispose();
        (system as IDisposable)?.Dispose();
    }
}
