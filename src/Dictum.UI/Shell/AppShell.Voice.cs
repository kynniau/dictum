using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Meetings.Voice;

namespace Dictum.UI.Shell;

/// <summary>
/// The voice print (design 1.6 C and D) for the shell: reading the sealed file, the CAM++ embedder the dictation filter keeps loaded
/// while "your voice only" is on, and the print handed to speaker separation on a one-track meeting.
/// </summary>
public sealed partial class AppShell
{
    private readonly object _voiceLock = new();
    private SherpaVoiceEmbedder? _filterEmbedder; // the dictation filter's own; Meetings and the card load theirs and dispose them
    private string? _filterEmbedderPath;
    private (DateTime Written, VoicePrint? Print, VoicePrintState State)? _voicePrintCache;

    /// <summary>The meetings owner key: the voice print is sealed under it (design C.2, one key, no second credential).</summary>
    internal IMeetingKeyring VoiceKeyring => new MeetingKeyring(Platform.Credentials);

    /// <summary>
    /// The print on disk and its state, re-read when the file changes. A print made with another model is never returned. A file that
    /// fails authentication throws (CryptographicException), as does a locked credential store (MeetingsLockedException).
    /// </summary>
    internal (VoicePrint? Print, VoicePrintState State) ReadVoicePrint()
    {
        var path = VoicePrintStore.PathFor(Paths);
        lock (_voiceLock)
        {
            if (!File.Exists(path)) { _voicePrintCache = null; return (null, VoicePrintState.Absent); }
            var written = File.GetLastWriteTimeUtc(path);
            if (_voicePrintCache is { } cached && cached.Written == written) return (cached.Print, cached.State);
            var (print, state) = VoicePrintStore.Read(path, VoiceKeyring, VoicePrintStore.CurrentModel);
            _voicePrintCache = (written, print, state);
            return (print, state);
        }
    }

    /// <summary>The usable print's embedding, or null (none, another model, or unreadable: the reason goes to the log).</summary>
    private float[]? UsableVoicePrint()
    {
        try { return ReadVoicePrint() is ({ } print, VoicePrintState.Ready) ? print.Embedding : null; }
        catch (Exception error)
        {
            _log.Warn($"The voice print could not be read ({error.GetType().Name}); it is not used.");
            return null;
        }
    }

    /// <summary>
    /// A new CAM++ embedder over the downloaded model, held to its pinned size and SHA-256 before its first load (ModelIntegrity), or
    /// null while the model is missing. The caller disposes it.
    /// </summary>
    internal SherpaVoiceEmbedder? CreateVoiceEmbedder()
    {
        if (SpeakerModels.Locate(Paths, _settings) is not { } model) return null;
        Integrity.Verify(model);
        return new SherpaVoiceEmbedder(model, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    }

    /// <summary>
    /// "Your voice only" for the dictation controller (design D). The print and the model are looked at only while the setting is on,
    /// so a user who never turns it on never loads either.
    /// </summary>
    internal OwnVoiceFilter CreateOwnVoiceFilter() => new(() => _settings, () =>
    {
        float[] print;
        try
        {
            switch (ReadVoicePrint())
            {
                case ({ } ready, VoicePrintState.Ready): print = ready.Embedding; break;
                case (_, VoicePrintState.OtherModel): return OwnVoiceSource.Missing(OwnVoiceSource.OtherModel);
                default: return OwnVoiceSource.Missing(OwnVoiceSource.NoPrint);
            }
        }
        catch (Exception error)
        {
            return OwnVoiceSource.Missing($"the voice print could not be read ({error.GetType().Name})");
        }
        lock (_voiceLock)
        {
            var model = SpeakerModels.Locate(Paths, _settings);
            if (model is null) return OwnVoiceSource.Missing(OwnVoiceSource.NoModel);
            if (_filterEmbedder is null || _filterEmbedderPath != model)
            {
                _filterEmbedder?.Dispose();
                _filterEmbedder = null;
                try { Integrity.Verify(model); }
                catch (Exception error) { return OwnVoiceSource.Missing($"the speaker model failed its check ({error.GetType().Name})"); }
                _filterEmbedder = new SherpaVoiceEmbedder(model, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
                _filterEmbedderPath = model;
            }
            return OwnVoiceSource.Ready(print, _filterEmbedder);
        }
    }, _log);

    /// <summary>The print for speaker separation (design C.4): only while meetings.use_voice_print is on, so "Identify speakers again" with it off runs without.</summary>
    private float[]? MeetingVoicePrint() => MeetingsSettings.UseVoicePrint ? UsableVoicePrint() : null;

    /// <summary>
    /// At start-up: deletes every voice-enrolment-* folder (a "Record my voice now" recording) that a quit or a crash mid-attempt left
    /// in the data folder, before anything else runs. A failure is logged and never stops the start; Delete all app data and Remove
    /// Dictum completely also list such folders (PathRegistry, category VoicePrint).
    /// </summary>
    private void DeleteVoiceEnrolmentLeftovers()
    {
        try
        {
            var deleted = VoiceEnrolment.DeleteLeftovers(Paths);
            if (deleted > 0) _log.Info($"Deleted {deleted} unfinished voice print recording folder(s) left from an earlier run.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"An unfinished voice print recording folder could not be deleted ({error.GetType().Name}); it is tried again at the next start.");
        }
    }

    /// <summary>
    /// "Delete all app data", with or without settings, deletes the voice print (orchestrator ruling 08/11/2026) and, before it does,
    /// turns both uses off exactly as "Delete my voice print" does (<see cref="VoiceEnrolment.TurnUsesOff"/>). With "including
    /// settings" ticked settings.json is deleted next anyway, so it is written only when it exists (never created for that scope).
    /// A settings.json that cannot be written is logged, and the print is still deleted: the person asked for all their data to go.
    /// </summary>
    private void TurnVoiceUsesOff(Dictum.Storage.RemovalScope scope, Dictum.Storage.RemovalPlan plan)
    {
        if (scope is not (Dictum.Storage.RemovalScope.AllData or Dictum.Storage.RemovalScope.AllDataIncludingSettings)) return;
        if (!plan.Items.Any(i => i.Category == Dictum.Storage.DataCategory.VoicePrint)) return;
        if (scope == Dictum.Storage.RemovalScope.AllDataIncludingSettings && !File.Exists(Paths.SettingsFile)) return;
        string? failure = null;
        if (!VoiceEnrolment.TurnUsesOff(keys => Models.TryWrite(out failure, reloadAfter: false,
                [.. keys.Select(k => (k.Key, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(k.Value)))])))
            _log.Warn($"The voice print uses could not be turned off in settings.json ({failure}); the print is deleted all the same.");
    }

    /// <summary>Releases the dictation filter's model at quit.</summary>
    private void DisposeVoice()
    {
        lock (_voiceLock)
        {
            _filterEmbedder?.Dispose();
            _filterEmbedder = null;
        }
    }
}
