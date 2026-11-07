using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dictum.Llm;
using Dictum.Core;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Meetings.Summaries;
using Dictum.Settings;
using Dictum.UI.Views.Meetings;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    private MeetingsWindow? _meetingsWindow;
    private readonly SemaphoreSlim _meetingSummaryGate = new(1, 1);
    private Func<float>? _meetingLevel;
    internal bool MeetingOwnsPill => Volatile.Read(ref _meetingLevel) is not null;
    internal void PresentMeeting(Func<float>? level, PillView view)
    {
        Volatile.Write(ref _meetingLevel, level);
        (_overlays ??= new Overlays.OverlayPresenter(this)).Show(new PillState(view, Environment.TickCount64));
    }
    internal void PauseMeetingFromPill() => _meetingsWindow?.PauseFromPill();
    internal void StopMeetingFromPill() => _meetingsWindow?.StopFromPill();
    internal MeetingsWindow CreateMeetingsWindow(MeetingsHost host) =>
        _meetingsWindow = new MeetingsWindow(host, () => DictationIdle, PresentMeeting);

    internal void ConfigureMeetingsShutdown(IClassicDesktopStyleApplicationLifetime? lifetime)
    {
        _lifetime = lifetime;
        if (lifetime is null) return;
        // ShutdownRequested is the first-chance veto, including Windows logout/shutdown.
        lifetime.ShutdownRequested += (_, request) =>
        {
            if (!TryShutdownMeetings()) request.Cancel = true;
        };
    }

    private bool TryShutdownMeetings()
    {
        if (_meetingsWindow is not { } window) return true;
        window.ShutdownReady -= ResumeMeetingsExit;
        window.ShutdownReady += ResumeMeetingsExit;
        return window.Shutdown();
    }

    private void ResumeMeetingsExit() => Dispatcher.UIThread.Post(Quit);

    internal MeetingsSettings MeetingsSettings => SettingsLoader.LoadFile(Paths.SettingsFile).Meetings;
    internal MeetingsHost CreateMeetingsHost() => MeetingsHost.Create(Paths, () => MeetingsSettings, Platform.Credentials,
        new MeetingTranscriberFactory(cancellation => _speech.GetAsync(cancellation), _voiceGates.Get,
            () => _settings.SilenceThreshold, Personal.Text.Dictionary),
        () => Platform.CreateCapture(_log), MeetingSummaryClient, CopyText, Platform.AudioDecoder) with
        {
            Log = _log,
            CopyOutOfHistory = CopySecret,
            LocalSummaryModel = EffectiveMeetingSummaryModel,
            RemoteSummaryModel = () => _settings.Llm.Provider == LlmSettings.Anthropic && CleanupCatalog.IsValidSetting(_settings.Llm.Model)
                ? LlmSettings.AnthropicDefaultModel : _settings.Llm.Model,
            LocalSummaryClient = LoadMeetingSummaryModel,
            SummaryModelInstalled = () => MeetingSummaryModelPath() is not null,
            OpenModels = () => OpenSettings("Models"),
            SummaryAttribution = MeetingsWindow.SummaryAttribution,
            LoopbackCapture = () => Platform.CreateLoopbackCapture(_log),
            SummaryHost = () => _settings.Llm.Provider == LlmSettings.Anthropic
                ? new Uri(_settings.Llm.BaseUrl ?? AnthropicLlmClient.DefaultBaseUrl).Host
                : CloudPolicy.CloudHost(_settings.Llm) ?? CloudPolicy.LocalServerHost(_settings.Llm),
            MicrophoneDevice = () => _settings.Audio.Device,
            SilenceThreshold = () => _settings.SilenceThreshold,
            CloudSpeechHost = () => _speech.SendsAudio ? _speech.Host : null,
            SpeakerModelsReady = () => SpeakerModels.Locate(Paths, _settings) is not null,
            SpeakerSeparator = CreateSpeakerSeparator,
        };

    /// <summary>
    /// Speaker separation over the downloaded CAM++ model (1.6 B), size- and SHA-256-checked before its first load like every model
    /// (ModelIntegrity); null while it is missing. Runs on the CPU with up to four threads.
    /// </summary>
    private SpeakerSeparator? CreateSpeakerSeparator(IMeetingStore store)
    {
        if (SpeakerModels.Locate(Paths, _settings) is not { } embedding) return null;
        Integrity.Verify(embedding);
        return new SpeakerSeparator(store, new SherpaVoiceEmbedder(embedding, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)),
            SpeakerModels.Tag(SpeakerModels.Embedding));
    }

    private string EffectiveMeetingSummaryModel() => MeetingsSettings.SummaryModel ??
        (_settings.Llm.Provider == LlmSettings.Local
            ? Path.GetFileName(SettingsFile.CleanupModelPath(_settings, Paths))
            : CleanupCatalog.FileNameFor(CleanupCatalog.Recommend(GpuMachine).Model));

    private string? MeetingSummaryModelPath()
    {
        var file = EffectiveMeetingSummaryModel();
        var info = CleanupCatalog.FindByFile(file);
        if (info is null || Path.GetFileName(file) != file) return null;
        return ModelFolders.Find(file, info.Bytes, Paths, _settings);
    }

    private async Task<ILlmClient> LoadMeetingSummaryModel(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try { await _meetingSummaryGate.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException(MeetingsText.PROPOSED_ModelLoadFailed); }
        var path = MeetingSummaryModelPath();
        // simplification: keep dictation's model resident and give Meetings one additional manager per summary.
        // Memory ceiling: the speech model, the clean-up model and one summary model plus their contexts can coexist.
        // A gate also covers a timed-out native load until it releases its allocation. Retire for a shared memory budget.
        var manager = new LocalLlmManager(_log, Integrity);
        try
        {
            var client = await MeetingLocalModel.For(manager).Load(path, _settings.Llm.Gpu,
                MeetingsText.PROPOSED_ModelMissing, MeetingsText.PROPOSED_ModelLoadFailed, deadline.Token);
            return new MeetingLocalLease(client, manager, _meetingSummaryGate);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            _ = ReleaseMeetingModel(manager);
            throw new TimeoutException(MeetingsText.PROPOSED_ModelLoadFailed);
        }
        catch { _ = ReleaseMeetingModel(manager); throw; }
    }

    private async Task ReleaseMeetingModel(LocalLlmManager manager)
    {
        try { await manager.ReleaseAsync(); }
        finally { manager.Dispose(); _meetingSummaryGate.Release(); }
    }

    private sealed class MeetingLocalLease(ILlmClient client, LocalLlmManager manager, SemaphoreSlim gate) : ILlmClient, IDisposable
    {
        private int _disposed;
        public string Name => client.Name;
        public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken) =>
            client.CompleteAsync(systemPrompt, rawTranscript, cancellationToken);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { manager.Dispose(); gate.Release(); } }
    }

    internal void OpenMeetings(bool start = false)
    {
        _flyout?.Close();
        if (start && !MeetingsSettings.Enabled) return;
        if (_meetingsWindow is null)
        {
            _meetingsWindow = CreateMeetingsWindow(CreateMeetingsHost());
            var opened = _meetingsWindow;
            Changed += opened.RefreshSettings;
            _meetingsWindow.Closed += (_, _) => { Changed -= opened.RefreshSettings; _meetingsWindow = null; };
            _meetingsWindow.Show();
        }
        _meetingsWindow.Activate();
        if (start) _meetingsWindow.BeginAgreement();
    }

    private Task<ILlmClient> MeetingSummaryClient(SummaryEngine engine, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (engine == SummaryEngine.Local)
            return LoadMeetingSummaryModel(cancellation);
        // Use the running app's effective configuration, including session withdrawals and offline-only.
        var llm = _settings.Llm;
        if (llm.Provider is not (LlmSettings.Anthropic or LlmSettings.OpenAiCompatible)
            || engine == SummaryEngine.Ollama && CloudPolicy.LocalServerHost(llm) is null
            || engine == SummaryEngine.OwnKey && CloudPolicy.CloudHost(llm) is null
            || !CloudPolicy.HasConsent(llm) || CloudPolicy.InsecureEndpoint(llm) is not null
            || TranscriptCleaner.OfflineRefusal(llm, _settings.OfflineOnly) is not null)
            throw new InvalidOperationException(MeetingsText.PROPOSED_ConfigureSummary);
        var stored = Vault.Snapshot(out var problem);
        if (problem is not null) throw new InvalidOperationException(MeetingsText.PROPOSED_ConfigureSummary);
        var credentials = SecretStore.Load(Paths, Environment.GetEnvironmentVariable, _settings.Keys.Active, stored);
        if (llm.Provider == LlmSettings.OpenAiCompatible)
        {
            if (llm.BaseUrl is null || !CloudPresets.ForAddress(llm.BaseUrl).TryGetKey(credentials, out var apiCredential))
                throw new InvalidOperationException(MeetingsText.PROPOSED_ConfigureSummary);
            return Task.FromResult<ILlmClient>(new MeetingClientLease(this,
                new OpenAiCompatibleLlmClient(_http, llm.BaseUrl, llm.Model, apiCredential), llm));
        }
        var anthropicCredential = credentials.Get(SecretStore.AnthropicKey);
        if (anthropicCredential is null) throw new InvalidOperationException(MeetingsText.PROPOSED_ConfigureSummary);
        return Task.FromResult<ILlmClient>(new MeetingClientLease(this, new AnthropicLlmClient(anthropicCredential,
            CleanupCatalog.IsValidSetting(llm.Model) ? LlmSettings.AnthropicDefaultModel : llm.Model,
            TimeSpan.FromMilliseconds(llm.TimeoutMs), llm.BaseUrl, _http), llm));
    }

    /// <summary>A withdrawn clean-up consent or changed endpoint stops this meeting's outstanding request too.</summary>
    private sealed class MeetingClientLease : ILlmClient, IDisposable
    {
        private readonly AppShell _shell;
        private readonly ILlmClient _client;
        private readonly LlmSettings _configured;
        private readonly CancellationTokenSource _withdrawn = new();
        public MeetingClientLease(AppShell shell, ILlmClient client, LlmSettings configured)
        {
            (_shell, _client, _configured) = (shell, client, configured);
            shell.Changed += Check;
        }
        private void Check()
        {
            var settings = _shell._settings;
            if (settings.Llm != _configured || !CloudPolicy.HasConsent(settings.Llm)
                || TranscriptCleaner.OfflineRefusal(settings.Llm, settings.OfflineOnly) is not null)
                _withdrawn.Cancel();
        }
        public string Name => _client.Name;
        public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            Check();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _withdrawn.Token);
            linked.Token.ThrowIfCancellationRequested();
            return await _client.CompleteAsync(systemPrompt, rawTranscript, linked.Token);
        }
        public void Dispose()
        {
            _shell.Changed -= Check;
            _withdrawn.Cancel(); _withdrawn.Dispose();
            (_client as IDisposable)?.Dispose();
        }
    }
}
