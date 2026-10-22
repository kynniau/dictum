using System.Diagnostics;
using Dictum.Calibration;
using Dictum.Settings;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    /// <summary>What the calibration window needs from the running app; every write goes through the app's own paths.</summary>
    internal CalibrationHost CreateCalibrationHost() => new(Paths, () => _settings, () => Personal.Text, Integrity, _log, Models.Find,
        values => Models.Write(values),
        DownloadSpeechModel: (name, then, finished) => Personal.DownloadForCalibration(name, then, finished),
        AddDictionaryTerms: Personal.AddDictionaryTerms,
        AddCorrections: Personal.AddCorrections,
        MovingFiles: () => Models.MovingFiles);

    /// <summary>Fix last dictation → Copy: the clipboard, kept out of clipboard history unless insert.clipboard_history is on.</summary>
    public void CopyText(string text) => Platform.Clipboard.SetText(text, keepOutOfHistory: !_settings.Insert.ClipboardHistory);

    /// <summary>
    /// Fix last dictation → Paste fix: hands the foreground back to the window the dictation went to, then pastes through the
    /// normal insert path. Returns what happened, for the window.
    /// </summary>
    public async Task<string> PasteFixAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "There is no text to paste.";
        if (Personal.Last is not { } last || !Platform.WindowFlags.IsWindow(last.Target)) return "The window the dictation went to has closed. Use Copy, then paste it yourself.";
        Platform.WindowFlags.TryActivate(last.Target);
        await Task.Delay(150);
        if (Platform.WindowFlags.ForegroundWindow() != last.Target) return "Windows did not let Dictum switch back to that window. Use Copy, then paste it yourself.";
        await InsertAsync(text, CancellationToken.None);
        Personal.ReplaceLast(text);
        return "Pasted. Select the old text there first next time if you want the fix to replace it.";
    }

    /// <summary>
    /// Settings → Microphone → Test: opens the chosen microphone for up to five seconds, reporting its level as it goes, then
    /// closes it and returns the loudest level heard. Nothing is kept.
    /// </summary>
    public async Task<float> TestMicrophoneAsync(string? device, Action<float> level, CancellationToken token)
    {
        var capture = Platform.CreateCapture(_log);
        capture.Start(device, Stopwatch.GetTimestamp());
        var peak = 0f;
        try
        {
            var end = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < end && !token.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None);
                var now = capture.CurrentLevel;
                peak = Math.Max(peak, now);
                level(now);
            }
        }
        finally
        {
            capture.Stop(); // the audio is dropped; the system's microphone indicator goes off
            (capture as IDisposable)?.Dispose();
        }
        return peak;
    }

    /// <summary>Where a file under the app-data folders really is (packaged builds are redirected, SPEC §18.2).</summary>
    public string RealPath(string path) => Platform.Package.RealPath(path);

    /// <summary>The models folder in use.</summary>
    public string ModelsDir => Paths.ModelsDir(_settings);

    /// <summary>Opens a file or folder with the system (creating a missing folder first), or says why not.</summary>
    public string? OpenPath(string path, bool folder)
    {
        try
        {
            if (folder) Directory.CreateDirectory(path);
            else if (!File.Exists(path)) return $"{path} does not exist yet.";
            Platform.Open(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path} could not be opened: {ex.Message}";
        }
    }

    /// <summary>Settings → About → Open settings file: creates settings.json with its defaults first if it is missing.</summary>
    public string? OpenSettingsFile()
    {
        SettingsFile.CreateIfMissing(Paths.SettingsFile, _settings.Whisper.Model);
        return OpenPath(Paths.SettingsFile, folder: false);
    }
}
