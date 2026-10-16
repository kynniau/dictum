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

    public static StateMachineOptions Options(AppSettings s) => new(s.TapMs, s.DoubleTapMs, s.HandsFreeMaxSeconds, StateMachineOptions.DefaultHoldMaxSeconds);

    /// <summary>
    /// The cleanup-provider, model-download and Check for updates clients: each refuses any off-machine request while
    /// offline_only is on (read per request). The update client is built like the download client (TLS 1.2, no proxy for
    /// this machine) but follows no redirect: a redirect is followed inside the socket handler, after the guard, so any host
    /// could answer for dictum.kynni.au (lens probe, 16/10/2026); a 3xx is a failed check instead. The download client keeps
    /// following them (huggingface.co redirects to its CDN). <paramref name="downloadHandler"/> and
    /// <paramref name="updateHandler"/> replace those two clients' socket handlers under their guards (tests).
    /// </summary>
    public static (HttpClient Providers, HttpClient Downloads, HttpClient Updates) CreateClients(Func<bool> offlineOnly,
        HttpMessageHandler? downloadHandler = null, HttpMessageHandler? updateHandler = null) =>
        (OfflineGuardHandler.CreateClient(offlineOnly), ModelDownloader.CreateHttpClient(offlineOnly, inner: downloadHandler),
            ModelDownloader.CreateHttpClient(offlineOnly, inner: updateHandler ?? ModelDownloader.CreateDownloadHandler(followRedirects: false)));

    /// <summary>Only these two settings shape the hotkey listener; anything else leaves the running one (and its key state) alone.</summary>
    public static bool HotkeyChanged(AppSettings before, AppSettings after) =>
        !string.Equals(before.Hotkey, after.Hotkey, StringComparison.Ordinal) || before.SuppressHotkey != after.SuppressHotkey;

    private static readonly string[] PersonalFiles = ["dictionary.txt", "corrections.txt", "snippets.txt", "profanity.txt"];

    /// <summary>
    /// Which config-folder changes reload: settings.json, consent.json, .env and the personal text files. Deleting a text
    /// file switches its feature off (SPEC §16), deleting or renaming .env drops its keys at once, and deleting consent.json
    /// withdraws the consent at once. Deleting settings.json does not reload (the settings in use stay until the file is
    /// back). A change of attributes is a change too: clearing read-only on settings.json or consent.json lets the reload
    /// save what this session holds (AppShell.SaveSessionChoices).
    /// </summary>
    public static bool ReloadsOn(WatcherChangeTypes change, string? name, string? oldName)
    {
        static bool Personal(string? n) => n is not null && PersonalFiles.Contains(n, StringComparer.OrdinalIgnoreCase);
        static bool Watched(string? n) => n is "settings.json" or "consent.json" or ".env" || Personal(n);
        return change switch
        {
            WatcherChangeTypes.Deleted => name is ".env" or "consent.json" || Personal(name),
            WatcherChangeTypes.Renamed => Watched(name) || Watched(oldName), // renamed away counts as deleted
            _ => Watched(name),
        };
    }
}
