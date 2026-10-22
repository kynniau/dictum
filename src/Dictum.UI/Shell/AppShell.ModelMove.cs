namespace Dictum.UI.Shell;

/// <summary>
/// The shell's part in moving the model files when the model folder changes (1.3; Settings > Models and Storage through
/// <see cref="Views.Settings.ModelFolderActions"/>): releasing both engines so no model file is held open, the models in use,
/// quitting mid-move, and the hash seam. Whether a move runs is <see cref="App.ModelCoordinator.MovingFiles"/>.
/// </summary>
public sealed partial class AppShell
{
    private bool _quitAfterMove;

    /// <summary>
    /// Releases the speech and clean-up models and waits for any load still under way to finish and release its engine
    /// (<see cref="Transcribe.ModelManager.ReleaseAsync"/>, <see cref="Llm.LocalLlmManager.ReleaseAsync"/>): an engine holds its
    /// model file open, so the original could not be deleted until then. Dictation has no model until the reload after the
    /// move, as after Storage's Delete of a model in use.
    /// </summary>
    internal async Task ReleaseModelsAsync()
    {
        await _models.ReleaseAsync();
        await _llm.ReleaseAsync();
    }

    /// <summary>The model files in use now (speech, and clean-up when local): the move carries them last.</summary>
    internal IReadOnlyList<string> ModelFilesInUse() => [.. ModelsInUse().OfType<string>()];

    /// <summary>Cancels the move in progress (quit); null while none runs.</summary>
    internal CancellationTokenSource? MoveCancel { get; set; }

    /// <summary>
    /// Quit while files move: the move is cancelled (the file being copied stops, its copy deleted and its original kept; a file
    /// already checked finishes its rename and delete) and Dictum quits when it has stopped (<see cref="QuitIfAskedDuringMove"/>).
    /// True when the quit was deferred.
    /// </summary>
    private bool DeferQuitForMove()
    {
        if (MoveCancel is not { } cancel) return false;
        _quitAfterMove = true;
        _log.Info("Quit asked while model files move: the move stops after the current step, then Dictum quits.");
        cancel.Cancel();
        return true;
    }

    /// <summary>Called when a move has ended: quits if Quit was asked meanwhile. True when it quit.</summary>
    internal bool QuitIfAskedDuringMove()
    {
        if (!_quitAfterMove) return false;
        _quitAfterMove = false;
        Quit();
        return true;
    }

    /// <summary>The SHA-256 the move checks each copy with; tests set a fake (their model files are not the real models). Null: the real hash.</summary>
    internal Func<string, CancellationToken, Task<string>>? MoveHasher { get; set; }
}
