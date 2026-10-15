using Avalonia.Threading;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.UI.Shell;

/// <summary>What the tray panel's header shows: the state word, its colour, and the models line.</summary>
/// <param name="Tone">"ready", "recording", "working", "error" or "off": which token colours the dot.</param>
public sealed record ShellStatus(string State, string Tone, string Models, string Tooltip, string? Download, double? DownloadFraction);

public sealed partial class AppShell
{
    private TrayState _trayState = TrayState.Idle;
    private string? _lastTooltip;

    /// <summary>The header line and the tooltip, built from the models, their runtimes, the hotkey and any download.</summary>
    public ShellStatus Status
    {
        get
        {
            var download = Models.DownloadStatus;
            var speech = Models.CurrentName;
            var runtime = _models.IsLoading ? "loading…" : _models.LastError is not null ? "model error" : WhisperEngine.LoadedRuntime;
            var hotkey = HotkeyParser.TryParse(_settings.Hotkey, out var spec, out _) ? spec.Display : _settings.Hotkey;
            var models = $"{speech} · {runtime} · {CleanupLabel()} · {hotkey}";
            var (state, tone) = !_settings.Enabled ? ("Off", "off")
                : _trayState switch
                {
                    TrayState.Recording => ("Recording", "recording"),
                    TrayState.Processing => ("Working", "working"),
                    TrayState.Error => ("Error", "error"),
                    _ when download is not null => ("Downloading", "working"),
                    _ when _models.IsLoading => ("Loading", "working"),
                    _ when !_models.IsReady => (_models.LastError is not null ? "Model error" : "No model", "error"),
                    _ => ("Ready", "ready"),
                };
            var tooltip = $"Dictum · {speech} · {download ?? runtime} · {CleanupLabel()} · {_settings.Hotkey}{(_settings.Enabled ? "" : " (off)")}";
            return new ShellStatus(state, tone, models, tooltip, download, Models.DownloadFraction);
        }
    }

    /// <summary>"qwen2.5-1.5b · Vulkan", "rules", "anthropic"…; the backend only for the local provider.</summary>
    private string CleanupLabel()
    {
        var name = Models.CurrentCleanupName;
        if (_settings.Llm.Provider != LlmSettings.Local) return name;
        var state = _llm.IsLoading ? "loading…" : _llm.Ready is not null ? LocalLlmEngine.Backend : _llm.LastError is not null ? "error" : "rules until downloaded";
        return $"{name} · {state}";
    }

    /// <summary>The privacy line at the foot of the tray panel: true when no dictation leaves this PC.</summary>
    public bool NothingLeaves => CloudPolicy.CloudHost(_settings.Llm) is null || _settings.OfflineOnly || !_cleaner.UsesModel;

    /// <summary>Recomputes the tooltip and tells the open windows to refresh. Safe from any thread.</summary>
    internal void StatusChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StatusChanged);
            return;
        }
        var status = Status;
        if (_tray is not null && status.Tooltip != _lastTooltip)
        {
            _lastTooltip = status.Tooltip;
            _tray.SetTooltip(status.Tooltip);
            if (status.Download is null) _log.Info($"Tray tooltip: {status.Tooltip}");
        }
        Changed?.Invoke();
    }

    /// <summary>From the controller (any thread): the tray icon, the start/stop sounds and the panel's state word.</summary>
    internal void SetTrayState(TrayState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetTrayState(state));
            return;
        }
        var previous = _trayState;
        _trayState = state;
        _tray?.SetState(state);
        if (_settings.Sounds.Enabled)
        {
            if (state == TrayState.Recording && previous != TrayState.Recording) Platform.PlayCue(SoundCue.Start);
            else if (state == TrayState.Processing && previous == TrayState.Recording) Platform.PlayCue(SoundCue.Stop);
        }
        StatusChanged();
    }

    /// <summary>What the controller needs from the UI; never blocks the caller.</summary>
    private sealed class UiBridge(AppShell shell) : IDictationUi
    {
        public void ShowPill(PillState state) => Dispatcher.UIThread.Post(() => shell._overlays?.Show(state));

        public void SetTrayState(TrayState state) => shell.SetTrayState(state);
    }
}
