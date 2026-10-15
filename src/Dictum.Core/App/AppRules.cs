using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.App;

/// <summary>The tray app's small decisions, kept apart from the UI so they are tested without one.</summary>
public static class AppRules
{
    /// <summary>
    /// Which first-run pickers to show: both without a settings.json, and with one only the choices it leaves open (an admin
    /// seed with offline_only alone still gets both pickers; one naming whisper.model gets the cleanup picker). A file that
    /// cannot be read gets none: its choices are unknown and it is never written over.
    /// </summary>
    public static (bool AskSpeech, bool AskCleanup) FirstRunQuestions(SettingsLoadResult file) =>
        file.Usable ? (!file.NamesSpeechModel, !file.NamesCleanupModel) : (false, false);

    public static StateMachineOptions Options(AppSettings s) => new(s.TapMs, s.DoubleTapMs, s.HandsFreeMaxSeconds);

    /// <summary>The cleanup-provider and model-download clients: each refuses any off-machine request while offline_only is on (read per request).</summary>
    public static (HttpClient Providers, HttpClient Downloads) CreateClients(Func<bool> offlineOnly) =>
        (OfflineGuardHandler.CreateClient(offlineOnly), ModelDownloader.CreateHttpClient(offlineOnly));

    /// <summary>Only these two settings shape the hotkey listener; anything else leaves the running one (and its key state) alone.</summary>
    public static bool HotkeyChanged(AppSettings before, AppSettings after) =>
        !string.Equals(before.Hotkey, after.Hotkey, StringComparison.Ordinal) || before.SuppressHotkey != after.SuppressHotkey;

    private static readonly string[] PersonalFiles = ["dictionary.txt", "corrections.txt", "profanity.txt"];

    /// <summary>
    /// Which config-folder changes reload: settings.json, .env and the personal text files. Deleting a text file switches
    /// its feature off (SPEC §16), and deleting or renaming .env drops its keys at once. Deleting settings.json does not
    /// reload (the settings in use stay until the file is back).
    /// </summary>
    public static bool ReloadsOn(WatcherChangeTypes change, string? name, string? oldName)
    {
        static bool Personal(string? n) => n is not null && PersonalFiles.Contains(n, StringComparer.OrdinalIgnoreCase);
        static bool Watched(string? n) => n is "settings.json" or ".env" || Personal(n);
        return change switch
        {
            WatcherChangeTypes.Deleted => name is ".env" || Personal(name),
            WatcherChangeTypes.Renamed => Watched(name) || Watched(oldName), // renamed away counts as deleted
            _ => Watched(name),
        };
    }
}
