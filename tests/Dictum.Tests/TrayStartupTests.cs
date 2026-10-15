using System.Diagnostics;
using Dictum.Core;

namespace Dictum.Tests;

/// <summary>
/// The tray app's startup, hot reload and quit with the batch-2 wiring, run as the real Dictum.exe in a child process and
/// fully sandboxed: scratch APPDATA and LOCALAPPDATA, a scratch config folder (settings.json, dictionary.txt,
/// corrections.txt), the hotkeys switched off (enabled = false, F24 / F23, so the keyboard hooks pass every key through),
/// the models folder of DICTUM_MODEL_PATH (which, by the documented precedence, is also the model that loads) and the
/// rule-based tidy. Gated by DICTUM_TRAY_TESTS=1 because a tray icon appears for about 12 s. Dictum's single-instance lock
/// is its own (Local\Dictum.SingleInstance), so an installed Dictate keeps running untouched.
/// </summary>
public class TrayStartupTests(ITestOutputHelper output)
{
    /// <summary>Dictum.exe from the app project's build output beside this test project's (same configuration).</summary>
    internal static string ExePath()
    {
        var probe = PillFocusTests.ProbePath(); // …\tests\Dictum.UiProbe\bin\<Configuration>\<tfm>\Dictum.UiProbe.exe
        var dir = new DirectoryInfo(Path.GetDirectoryName(probe)!);
        var root = dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName; // the repository
        return Path.Combine(root, "src", "Dictum.Windows", "bin", dir.Parent.Name, dir.Name, "Dictum.exe");
    }

    [Fact]
    public async Task Tray_app_starts_reloads_personal_files_and_quits_cleanly()
    {
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipUnless(TestEnv.Get("TRAY_TESTS") == "1" && !string.IsNullOrWhiteSpace(modelPath),
            "Set DICTUM_TRAY_TESTS=1 and DICTUM_MODEL_PATH (shows a tray icon for about 12 s).");
        var exe = ExePath();
        Assert.True(File.Exists(exe), $"{exe} is missing: build src\\Dictum.Windows");
        var models = Path.GetDirectoryName(Path.GetFullPath(modelPath!))!;
        using var scratch = new Scratch($$"""
            { "enabled": false, "hotkey": "F24", "fix_hotkey": "F23", "pill": { "enabled": false },
              "whisper": { "model": "base.en", "models_dir": {{System.Text.Json.JsonSerializer.Serialize(models)}} },
              "llm": { "provider": "none" }, "profanity": { "mode": "mask" } }
            """);
        File.WriteAllText(scratch.Paths.DictionaryFile, "Kynni\n");
        File.WriteAllText(scratch.Paths.CorrectionsFile, "report => Q3 report\n");
        var roaming = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "roaming")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "local")).FullName;

        var start = new ProcessStartInfo(exe, ["--config", scratch.Paths.ConfigDir, "--exit-after", "12"]) { UseShellExecute = false };
        start.Environment["APPDATA"] = roaming;
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["DICTUM_MODEL_PATH"] = modelPath;
        start.Environment.Remove("DICTATE_MODEL_PATH");
        using var process = Process.Start(start)!;
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        File.AppendAllText(scratch.Paths.DictionaryFile, "Xero\n"); // after the model has loaded: must reach whisper's prompt without a reload
        Assert.True(process.WaitForExit(90_000), "the tray app did not quit");
        Assert.Equal(0, process.ExitCode);

        var logs = Path.Combine(local, "Dictum", "logs");
        var lines = File.ReadAllLines(Directory.GetFiles(logs, "dictum-*.log").Single());
        foreach (var line in lines) output.WriteLine(line);
        var text = string.Join("\n", lines);
        Assert.Contains("Tray icon added to the notification area.", text);
        Assert.Contains("Personal files: dictionary 1 term(s), corrections 1 rule(s), snippets 0, profanity mask", text);
        Assert.Contains("Personal files: dictionary 2 term(s)", text); // the file watcher picked up the edit
        Assert.Contains("Speech decoding: greedy, dictionary prompt of 5 characters.", text); // "Kynni"
        Assert.Contains("Speech decoding: greedy, dictionary prompt of 11 characters.", text); // "Kynni, Xero"
        Assert.Equal(2, lines.Count(l => l.Contains("Input hook installed", StringComparison.Ordinal))); // hotkey and fix_hotkey
        Assert.Single(lines, l => l.Contains("Whisper model ", StringComparison.Ordinal) && l.Contains(" loaded in ", StringComparison.Ordinal));
        Assert.Contains("--exit-after 12: shutting down", text);
        Assert.Contains("Dictum exited.", text);
        Assert.DoesNotContain(lines, l => l.Contains(" ERROR ", StringComparison.Ordinal));
        Assert.DoesNotContain("Xero", text); // counts only, never the words
    }
}
