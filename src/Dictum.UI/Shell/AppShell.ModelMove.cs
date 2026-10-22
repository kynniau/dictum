namespace Dictum.UI.Shell;

/// <summary>
/// The shell's part in moving the model files when the model folder changes (1.3; Settings > Models and Storage through
/// <see cref="Views.Settings.ModelFolderActions"/>): releasing both engines so no model file is held open, and the hash seam.
/// </summary>
public sealed partial class AppShell
{
    /// <summary>
    /// Releases the speech and clean-up models and waits for any load still under way to finish and release its engine
    /// (<see cref="Transcribe.ModelManager.ReleaseAsync"/>, <see cref="Llm.LocalLlmManager.ReleaseAsync"/>): a loaded clean-up
    /// model holds its .gguf mapped, so the file cannot be moved or deleted until then. Dictation has no model until the next
    /// reload, as after Storage's Delete of a model in use.
    /// </summary>
    internal async Task ReleaseModelsAsync()
    {
        await _models.ReleaseAsync();
        await _llm.ReleaseAsync();
    }

    /// <summary>True while model files are being moved: a second folder change waits for the first to finish.</summary>
    internal bool MovingModels { get; set; }

    /// <summary>The SHA-256 the move checks each copy with; tests set a fake (their model files are not the real models). Null: the real hash.</summary>
    internal Func<string, CancellationToken, Task<string>>? MoveHasher { get; set; }
}
