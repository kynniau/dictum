using Dictum.Settings;

namespace Dictum.Cli;

public enum CliMode { Tray, Transcribe, SelfTest, DownloadModel, DownloadCleanupModel, BenchCleanup, PrintModelsDir, Help }

/// <param name="Model">--model: one-launch override of whisper.model (short name or absolute path).</param>
/// <param name="DownloadName">--download-model: catalogue/short name to fetch.</param>
public sealed record CliOptions(CliMode Mode, string? WavPath, bool NoLlm, string? ConfigDir, string? Model = null, string? DownloadName = null, int? ExitAfterSeconds = null, string? CleanupModel = null)
{
    public const string Usage =
        """
        Dictum - local voice dictation

          Dictum.exe                                  run in the system tray (normal mode)
          Dictum.exe --transcribe <file.wav> [--no-llm]
                                                       print the raw transcript (and the cleaned one unless
                                                       --no-llm) to stdout, then exit
          Dictum.exe --selftest                       load the models, report their runtimes, list
                                                       microphones, the data folders and the .env keys (never their
                                                       values), give you 5 s to press the hotkey; exit 0/1
          Dictum.exe --download-model <name>          download a model (e.g. base.en) to the models folder with
                                                       a progress line; exit 0/1
          Dictum.exe --download-cleanup-model <name>  download a cleanup model (e.g. qwen2.5-1.5b) the same way
          Dictum.exe --bench-cleanup <file.txt|file.wav>
                                                       run every downloaded cleanup model over the same input
                                                       (a .wav is transcribed first) and print each one's output,
                                                       time and validator verdict side by side
          Dictum.exe --print-models-dir               print the folder the app downloads models to and looks in
                                                       first (models.folder, else its earlier name
                                                       whisper.models_dir, else %LOCALAPPDATA%\Dictum\models);
                                                       install.ps1 uses it
          --model <name|path>                          use this model for this launch only (any mode)
          --cleanup-model <name|path>                  use this local cleanup model for this launch only (any mode)
          --config <dir>                               use <dir> instead of %APPDATA%\Dictum for settings.json/.env
          --exit-after <seconds>                       tray mode: quit the way Quit in the tray panel does after this
                                                       many seconds (scripted startup/shutdown checks)

        Dictum.exe is a Windows (GUI) program: from PowerShell pipe it to wait for the result, e.g.
          .\Dictum.exe --selftest | Out-Host
        """;

    /// <summary>Parses arguments. Returns null and an error message for bad input.</summary>
    public static (CliOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var mode = CliMode.Tray;
        string? wav = null, config = null, model = null, download = null, cleanupModel = null;
        int? exitAfter = null;
        var noLlm = false;
        string? modeFlag = null;
        string? Use(CliMode next, string flag) // one mode per launch: a second, different one is refused, never silently preferred
        {
            if (modeFlag is not null && modeFlag != flag) return $"{modeFlag} cannot be combined with {flag}.";
            (mode, modeFlag) = (next, flag);
            return null;
        }
        for (var i = 0; i < args.Count; i++)
        {
            // A value is never another flag or empty: PowerShell 5.1 drops an empty argument, so "--config $empty --selftest"
            // arrives as "--config --selftest".
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--transcribe":
                    wav = Next();
                    if (wav is null) return (null, "--transcribe needs a WAV file path.");
                    if (Use(CliMode.Transcribe, "--transcribe") is { } clashT) return (null, clashT);
                    break;
                case "--bench-cleanup":
                    wav = Next();
                    if (wav is null) return (null, "--bench-cleanup needs a .txt transcript or a .wav file.");
                    if (Use(CliMode.BenchCleanup, "--bench-cleanup") is { } clashB) return (null, clashB);
                    break;
                case "--no-llm":
                    noLlm = true;
                    break;
                case "--selftest":
                    if (Use(CliMode.SelfTest, "--selftest") is { } clashS) return (null, clashS);
                    break;
                case "--download-model":
                    download = Next();
                    if (download is null || !ModelCatalog.IsShortName(download)) return (null, "--download-model needs a model name such as base.en (see --help).");
                    if (Use(CliMode.DownloadModel, "--download-model") is { } clashD) return (null, clashD);
                    break;
                case "--download-cleanup-model":
                    download = Next();
                    if (download is null || CleanupCatalog.Find(download) is null)
                        return (null, $"--download-cleanup-model needs one of: {string.Join(", ", CleanupCatalog.Entries.Select(e => e.Name))}.");
                    if (Use(CliMode.DownloadCleanupModel, "--download-cleanup-model") is { } clashC) return (null, clashC);
                    break;
                case "--cleanup-model":
                    cleanupModel = Next();
                    if (cleanupModel is null || !CleanupCatalog.IsValidSetting(cleanupModel))
                        return (null, $"--cleanup-model needs one of: {string.Join(", ", CleanupCatalog.Entries.Select(e => e.Name))}, or an absolute path to a .gguf file.");
                    break;
                case "--model":
                    model = Next();
                    if (model is null || !ModelCatalog.IsValidModelSetting(model)) return (null, "--model needs a model name such as small.en or an absolute path to a ggml .bin file.");
                    break;
                case "--exit-after":
                    if (!int.TryParse(Next(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 86400)
                        return (null, "--exit-after needs a number of seconds from 1 to 86400.");
                    exitAfter = seconds;
                    break;
                case "--config":
                    config = Next();
                    if (config is null) return (null, "--config needs a folder path.");
                    break;
                case "--print-models-dir":
                    if (Use(CliMode.PrintModelsDir, "--print-models-dir") is { } clashP) return (null, clashP);
                    break;
                case "--help" or "-h" or "/?":
                    if (Use(CliMode.Help, "--help") is { } clashH) return (null, clashH);
                    break;
                default:
                    return (null, $"Unknown argument \"{args[i]}\".");
            }
        }
        if (noLlm && mode != CliMode.Transcribe) return (null, "--no-llm only applies to --transcribe.");
        if (exitAfter is not null && mode != CliMode.Tray) return (null, "--exit-after only applies to tray mode.");
        return (new CliOptions(mode, wav, noLlm, config, model, download, exitAfter, cleanupModel), null);
    }
}
