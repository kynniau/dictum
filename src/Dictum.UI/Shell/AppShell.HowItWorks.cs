using Dictum.Audio;
using Dictum.Core;

namespace Dictum.UI.Shell;

/// <summary>The one dictation pipeline, shared by the hotkey and Settings &gt; See how it works (SPEC §28).</summary>
public sealed partial class AppShell
{
    private DictationPipeline? _pipeline;

    /// <summary>
    /// The pipeline every dictation takes (the controller's), with the speech model, clean-up and finishing step in use. Built
    /// on first use, so the demonstration page has it in a shell that has not started (tests) as in the running app.
    /// </summary>
    internal DictationPipeline Pipeline => _pipeline ??= new DictationPipeline(_models.GetAsync, () => _cleaner, InsertAsync, () => _settings, _log,
        Personal.Finish, RememberPasted, names: () => Personal.Text.Dictionary.Terms);

    /// <summary>
    /// Settings &gt; See how it works: runs one recording, held in memory, through <see cref="Pipeline"/> with nothing pasted
    /// (<see cref="DictationPipeline.DemonstrateAsync"/>), off the UI thread. The callbacks come on the pipeline's thread.
    /// </summary>
    internal Task DemonstrateAsync(CapturedAudio audio, CancellationToken token, Action<PillView> pill, Action<PipelineProgress> report) =>
        Task.Run(() => Pipeline.DemonstrateAsync(audio, token, pill, report), token);
}
