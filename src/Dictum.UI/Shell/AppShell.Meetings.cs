using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dictum.Llm;
using Dictum.Meetings;
using Dictum.Settings;
using Dictum.UI.Views.Meetings;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    private MeetingsWindow? _meetingsWindow;
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
            LoopbackCapture = () => Platform.CreateLoopbackCapture(_log),
            SummaryHost = () => _settings.Llm.Provider == LlmSettings.Anthropic
                ? new Uri(_settings.Llm.BaseUrl ?? AnthropicLlmClient.DefaultBaseUrl).Host
                : CloudPolicy.CloudHost(_settings.Llm) ?? CloudPolicy.LocalServerHost(_settings.Llm),
            MicrophoneDevice = () => _settings.Audio.Device,
            SilenceThreshold = () => _settings.SilenceThreshold,
            CloudSpeechHost = () => _speech.SendsAudio ? _speech.Host : null,
        };

    internal void OpenMeetings(bool start = false)
    {
        _flyout?.Close();
        if (start && !MeetingsSettings.Enabled) return;
        if (_meetingsWindow is null)
        {
            _meetingsWindow = new MeetingsWindow(CreateMeetingsHost());
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
            return Task.FromResult<ILlmClient>(new LocalLlmClient(() => _llm.Ready, _settings.Llm.Model));
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
