using System.Text.Json.Nodes;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.UI.Shell;

/// <summary>What the tray panel's header shows: the state word, its colour, the models line and, under it, any model problem.</summary>
/// <param name="Tone">"ready", "recording", "working", "error" or "off": which token colours the dot.</param>
/// <param name="Problem">Why a model could not load, or runs on the CPU, in plain words when the cause is known
/// (<see cref="AppShell.ModelProblem"/>); null when nothing is wrong.</param>
/// <param name="TryCpu">"Try on the CPU" is offered (<see cref="AppShell.TryOnCpu"/>).</param>
public sealed record ShellStatus(string State, string Tone, string Models, string Tooltip, string? Download, double? DownloadFraction,
    string? Problem = null, bool TryCpu = false);

public sealed partial class AppShell
{
    /// <summary>The button beside a model problem. PROPOSED copy (17/10/2026), awaiting the product owner's review: do not reword.</summary>
    public const string TryOnCpuLabel = "Try on the CPU";

    /// <summary>
    /// After a model's reason when it failed on the CPU inside a GPU runtime with its gpu setting at cpu: the CPU-only runtime
    /// loads only at a start. PROPOSED copy (17/10/2026), awaiting the product owner's review: do not reword.
    /// </summary>
    public const string RestartForCpuHint = "Restart Dictum to use the CPU.";

    private TrayState _trayState = TrayState.Idle;
    private string? _lastTooltip;

    /// <summary>The header line and the tooltip, built from the models, their runtimes, the hotkey and any download.</summary>
    public ShellStatus Status
    {
        get
        {
            var download = Models.DownloadStatus;
            var speech = Models.CurrentName;
            var runtime = _models.IsLoading ? "loading…" : _models.LastError is not null ? "model error" : SpeechRuntime();
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
            return new ShellStatus(state, tone, models, tooltip, download, Models.DownloadFraction, ModelProblem, TryCpuOffered);
        }
    }

    /// <summary>
    /// The speech runtime: "CPU" for a model kept off the GPU inside a GPU runtime (whisper.gpu = cpu after it loaded, or the retry
    /// after the GPU failed), otherwise the runtime that loaded ("CPU (no AVX)" stays as it is).
    /// </summary>
    private string SpeechRuntime()
    {
        var loaded = WhisperEngine.LoadedRuntime;
        return _models.RunsOnCpu && !loaded.StartsWith("CPU", StringComparison.Ordinal) ? "CPU" : loaded;
    }

    /// <summary>"qwen2.5-1.5b · Vulkan", "rules", "anthropic"…; the backend only for the local provider.</summary>
    private string CleanupLabel()
    {
        var name = Models.CurrentCleanupName;
        if (_settings.Llm.Provider != LlmSettings.Local) return name;
        var state = _llm.IsLoading ? "loading…"
            : _llm.Ready is not null ? _llm.RunsOnCpu ? "CPU" : LocalLlmEngine.Backend
            : _llm.LastError is not null ? "error" : "rules until downloaded";
        return $"{name} · {state}";
    }

    /// <summary>
    /// The second line under the models line, in the tray panel and on About: why a model could not load, or why it runs on the
    /// CPU, in plain words when the cause is known (<see cref="ModelFailure"/>), else the engine's own message. One line names
    /// both models when they share the reason (a missing Visual C++ runtime stops both). Null while nothing is wrong, and for a
    /// model still loading. The line's prefixes are PROPOSED copy (17/10/2026), awaiting the product owner's review.
    /// </summary>
    internal string? ModelProblem => ProblemLine(
        _models.IsLoading ? null : Reason(_models.LastError, _models.RestartForCpu, _models.Notice),
        _settings.Llm.Provider != LlmSettings.Local || _llm.IsLoading ? null : Reason(_llm.LastError, _llm.RestartForCpu, _llm.Notice));

    /// <summary>A model's failure (with the restart hint when only a restart can put it on the CPU), else its notice.</summary>
    private static string? Reason(string? error, bool restartForCpu, string? notice) =>
        error is null ? notice : restartForCpu ? $"{Sentence(error)} {RestartForCpuHint}" : error;

    /// <summary>The problem line for each model's reason (null: nothing to say about it).</summary>
    internal static string? ProblemLine(string? speech, string? cleanup) => (speech, cleanup) switch
    {
        (null, null) => null,
        ({ } s, { } c) when s == c => $"Speech and clean-up models: {Sentence(s)}",
        ({ } s, null) => $"Speech model: {Sentence(s)}",
        (null, { } c) => $"Clean-up model: {Sentence(c)}",
        ({ } s, { } c) => $"Speech model: {Sentence(s)}\nClean-up model: {Sentence(c)}",
    };

    private static string Sentence(string reason) => reason.EndsWith('.') ? reason : reason + ".";

    /// <summary>Whether <see cref="TryOnCpu"/> has anything to do: a model failed inside a GPU runtime in a way the CPU might get past, and its gpu setting is not cpu.</summary>
    internal bool TryCpuOffered => CpuSettings().Count > 0;

    /// <summary>
    /// "Try on the CPU" beside a model problem: writes whisper.gpu and/or llm.gpu = "cpu" for each model that failed inside a GPU
    /// runtime in a way the CPU might get past, through the settings writer. Its reload loads the model again off the GPU at once
    /// (<see cref="WhisperEngine.Load"/>, <see cref="LocalLlmEngine.Load"/>); the CPU-only runtime itself loads at the next start,
    /// and if the model fails again the problem line asks for that restart (<see cref="RestartForCpuHint"/>). False when
    /// settings.json could not be written (the writer's balloon says so).
    /// </summary>
    public bool TryOnCpu()
    {
        var values = CpuSettings();
        if (values.Count == 0) return true;
        _log.Info($"Try on the CPU: {string.Join(" and ", values.Select(v => v.Key))} set to cpu.");
        return Write([.. values]);
    }

    private List<(string Key, JsonNode? Value)> CpuSettings()
    {
        var values = new List<(string Key, JsonNode? Value)>();
        if (_models.LastError is not null && _models.CpuMightHelp && _settings.Whisper.Gpu != "cpu") values.Add(("whisper.gpu", JsonValue.Create("cpu")));
        if (_settings.Llm.Provider == LlmSettings.Local && _llm.LastError is not null && _llm.CpuMightHelp && _settings.Llm.Gpu != "cpu")
            values.Add(("llm.gpu", JsonValue.Create("cpu")));
        return values;
    }

    /// <summary>
    /// The privacy line at the foot of the tray panel: true when no dictation's text goes to another program. An
    /// OpenAI-compatible server on this PC is not "nothing leaves" (<see cref="CloudPolicy.NothingLeaves"/>).
    /// </summary>
    public bool NothingLeaves => CloudPolicy.NothingLeaves(_settings.Llm, _cleaner.UsesModel);

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

    /// <summary>
    /// From the controller (any thread): the tray icon, the start/stop sounds, the panel's state word, and the dictation
    /// listener's rule (<see cref="ApplyHookEnabled"/>: a mouse hotkey stays on through a dictation and goes off when it ends
    /// with no speech model ready).
    /// </summary>
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
        ApplyHookEnabled();
    }

    /// <summary>What the controller needs from the UI; never blocks the caller.</summary>
    private sealed class UiBridge(AppShell shell) : IDictationUi
    {
        public void ShowPill(PillState state) => Dispatcher.UIThread.Post(() => shell._overlays?.Show(state));

        public void SetTrayState(TrayState state) => shell.SetTrayState(state);
    }
}
