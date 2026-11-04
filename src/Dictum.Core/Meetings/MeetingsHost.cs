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
    public const string PLACEHOLDER_UnrecordedProvider = "the service used for this summary (provider not recorded)";
    public const string PLACEHOLDER_ExportReminder = "Sharing a recording or transcript may need the other people's agreement.";
    public string Export(MeetingId id) => Store.ExportMarkdown(id) + Environment.NewLine + PLACEHOLDER_ExportReminder + Environment.NewLine;
    public static string Caption(string storedEngine)
    {
        // Summary has no provider field. New own-key summaries retain the client identity in Engine,
        // so later configuration changes cannot change the attribution on screen or in exports.
        const string prefix = "OwnKey:";
        return storedEngine.StartsWith(prefix, StringComparison.Ordinal)
            ? SummaryCaption.For("OwnKey", storedEngine[prefix.Length..])
            : storedEngine == "OwnKey" ? SummaryCaption.For("OwnKey", PLACEHOLDER_UnrecordedProvider)
            : SummaryCaption.For(storedEngine);
    }
    public Func<string?> MicrophoneDevice { get; init; } = () => null;
    public Func<double> SilenceThreshold { get; init; } = () => 0.01;
    public Func<string?> CloudSpeechHost { get; init; } = () => null;
    public Func<Task<string?>>? ChooseImport { get; init; }
    public static MeetingsHost Create(AppPaths paths, Func<MeetingsSettings> settings, ICredentialStore credentials,
        IMeetingTranscriberFactory factory, Func<IAudioCapture> capture,
        Func<SummaryEngine, CancellationToken, Task<ILlmClient>> summaryClient, Action<string> copy)
    {
        var snapshot = settings();
        var root = snapshot.Folder ?? Path.Combine(paths.LocalDir, "meetings");
        IMeetingStore store = new FileMeetingStore(paths, snapshot, new MeetingKeyring(credentials), Caption);
        var transcriber = factory.Create(store);
        // simplification: until IPlatform gains AudioDecoder and CreateLoopbackCapture (lane L2)
        var importer = new MeetingImporter(store, transcriber, new WavOnlyDecoder());
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
            () => credentials.Write(dismissal, computer, "dismissed"));
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
internal sealed class MicrophoneMeetingSource(IAudioCapture capture, string? device, double threshold) : IMeetingAudioSource, IDisposable
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
        // simplification: until IPlatform gains AudioDecoder and CreateLoopbackCapture (lane L2)
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
    public Stream Audio()
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
