using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Dictum.Cli;
using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>Fix batch, items 21 and 22 (#24, #25, #45, #46, #48, #52): catalogue pins, the downloader, the CLI and the scripts.</summary>
public sealed class FixBatchDownloadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-fixj-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dictum.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Dictum.sln not found above the test folder.");
    }

    [Fact]
    public void Every_catalogue_download_is_pinned_to_a_commit()
    {
        var uris = ModelCatalog.Entries.Select(e => e.Download.Uri).Concat(CleanupCatalog.Entries.Select(e => e.Download.Uri)).ToList();
        Assert.All(uris, uri => Assert.Matches(@"^https://huggingface\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}/[^/]+$", uri.AbsoluteUri));
    }

    /// <summary>Response content that counts how often it is read.</summary>
    private sealed class CountingContent(byte[] bytes) : HttpContent
    {
        public int Reads { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    private sealed class OneFile(HttpContent content) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task A_server_announcing_another_size_is_refused_before_the_file_is_read()
    {
        var content = new CountingContent(new byte[10]);
        var server = new OneFile(content);
        var spec = new DownloadSpec(new Uri("https://dictate-test.invalid/model.bin"), "model.bin", 4, null); // the catalogue says 4 bytes
        var target = Path.Combine(_dir.FullName, "model.bin");
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, offlineOnly: false, new Progress<(long, long?)>(), Ct));
        Assert.Contains("10", ex.Message, StringComparison.Ordinal);
        Assert.Equal((1, 0), (server.Requests, content.Reads)); // one request, nothing streamed, no retries
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Offline_only_makes_the_cli_download_exit_1()
    {
        // A made-up spec on a host that never resolves, and a counting client: even with every offline check broken, this
        // test could not download the real 491 MB file (lens finding, 15/10/2026).
        var spec = new DownloadSpec(new Uri("https://dictate-test.invalid/model.gguf"), "model.gguf", 1_000, null);
        var server = new CountingHandler();
        Assert.Equal(1, await DownloadCommand.DownloadAsync(spec, Path.Combine(_dir.FullName, spec.FileName), offlineOnly: true, NullLog.Instance, http: new HttpClient(server)));
        Assert.Equal(0, server.Requests);
        Assert.False(File.Exists(Path.Combine(_dir.FullName, spec.FileName)));
    }

    [Theory]
    [InlineData("--config|--selftest", "--config")] // PowerShell 5.1 drops an empty $cfg: tray mode in a folder called --selftest
    [InlineData("--config|", "--config")] // the real %APPDATA%\Dictum, silently
    [InlineData("--transcribe|--no-llm", "--transcribe")]
    [InlineData("--selftest|--download-cleanup-model|qwen2.5-1.5b", "cannot be combined")] // a 1.1 GB download instead of a check
    [InlineData("--download-model|base.en|--selftest", "cannot be combined")]
    public void A_flag_never_takes_another_flag_or_nothing_as_its_value(string args, string expected)
    {
        var (options, error) = CliOptions.Parse(args.Split('|'));
        Assert.Null(options);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_models_folder_is_printed_the_way_the_app_resolves_it()
    {
        var paths = new AppPaths(_dir.CreateSubdirectory("cfg").FullName, Path.Combine(_dir.FullName, "local"), _dir.FullName);
        Assert.Equal((CliMode.PrintModelsDir, paths.ConfigDir), CliOptions.Parse(["--print-models-dir", "--config", paths.ConfigDir]).Options is { } o ? (o.Mode, o.ConfigDir) : default);

        string Print(out int exit)
        {
            using var output = new StringWriter();
            exit = CliCommands.PrintModelsDir(paths, output, TextWriter.Null);
            return output.ToString().Trim();
        }
        Assert.Equal(paths.DefaultModelsDir, Print(out var exit));
        Assert.Equal(0, exit);
        var custom = Path.Combine(_dir.FullName, "custom-models");
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(new { whisper = new { models_dir = custom } }));
        Assert.Equal(custom, Print(out exit));
        File.WriteAllText(paths.SettingsFile, """{ "whisper": { "models_dir": """); // broken: never guess a folder to copy into
        Assert.Equal("", Print(out exit));
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Bench_cleanup_reports_an_unreadable_wav_and_exits_1()
    {
        var paths = new AppPaths(_dir.CreateSubdirectory("cfg-bench").FullName, Path.Combine(_dir.FullName, "local-bench"), _dir.FullName);
        var wav = Path.Combine(_dir.FullName, "not-audio.wav");
        File.WriteAllText(wav, "this is not a RIFF file");
        File.WriteAllText(paths.SettingsFile, TestTerms.Json); // the Terms accepted: the command reaches the WAV
        var (options, _) = CliOptions.Parse(["--bench-cleanup", wav]);
        Assert.Equal(1, await BenchCleanupCommand.RunAsync(options!, paths, NullLog.Instance, new SecretRedactor())); // it used to crash
    }

    private static (int Exit, string Output) RunScript(string script, string arguments, string scratch)
    {
        var psi = new ProcessStartInfo(SystemPrograms.WindowsPowerShell(SystemPrograms.Root)!, // by full path, never by bare name
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(RepoRoot(), "scripts", script)}\" {arguments}")
        {
            UseShellExecute = false,
            CreateNoWindow = true, // no console window: nothing can take focus from the user's own work
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (variable, folder) in new[] { ("LOCALAPPDATA", "local"), ("APPDATA", "roaming"), ("TEMP", "temp"), ("TMP", "temp") })
        {
            psi.Environment[variable] = Directory.CreateDirectory(Path.Combine(scratch, folder)).FullName;
        }
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{script} did not finish in 2 minutes.");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>The app's own managed files from this test's output folder (its native runtimes are not needed to print a folder).</summary>
    private static string MinimalBuild(string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Dictum.Tests", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, name));
        }
        return target;
    }

    [Fact]
    public void Install_copies_models_where_the_app_looks_keeps_the_exe_env_and_leaves_settings_writable()
    {
        TestEnv.WindowsOnly("Runs install.ps1 in Windows PowerShell against a Dictum.exe layout.");
        var scratch = _dir.CreateSubdirectory("install").FullName;
        var source = MinimalBuild(Path.Combine(scratch, "source"));
        var share = Directory.CreateDirectory(Path.Combine(scratch, "share")).FullName;
        File.WriteAllBytes(Path.Combine(share, "ggml-tiny.en.bin"), new byte[1000]);
        var models = Path.Combine(scratch, "custom-models");
        var seed = Path.Combine(scratch, "seed.json");
        File.WriteAllText(seed, JsonSerializer.Serialize(new { whisper = new { models_dir = models } }));
        File.SetAttributes(seed, FileAttributes.ReadOnly); // as copied from a read-only share
        var installDir = Directory.CreateDirectory(Path.Combine(scratch, "local", "Programs", "Dictum")).FullName; // an earlier install
        File.Copy(Path.Combine(source, "Dictum.exe"), Path.Combine(installDir, "Dictum.exe"));
        File.WriteAllText(Path.Combine(installDir, ".env"), "ANTHROPIC_API_KEY=sk-ant-test-only-not-a-key\n");

        var (exit, output) = RunScript("install.ps1", $"-Source \"{source}\" -Settings \"{seed}\" -ModelsFrom \"{share}\" -Force", scratch);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(models, "ggml-tiny.en.bin")), "the model was not copied into whisper.models_dir:\n" + output);
        Assert.False(File.Exists(Path.Combine(scratch, "local", "Dictum", "models", "ggml-tiny.en.bin")));
        Assert.Equal("ANTHROPIC_API_KEY=sk-ant-test-only-not-a-key", File.ReadAllText(Path.Combine(installDir, ".env")).Trim()); // kept, even with -Force
        Assert.Empty(Directory.GetFiles(Path.Combine(scratch, "temp"), "dictate-env-*")); // and never copied to %TEMP%
        Assert.False(new FileInfo(Path.Combine(scratch, "roaming", "Dictum", "settings.json")).IsReadOnly); // the tray can still save choices
    }

    [Fact]
    public void Uninstall_in_a_non_interactive_session_removes_the_program_and_keeps_user_data()
    {
        TestEnv.WindowsOnly("Runs uninstall.ps1 in Windows PowerShell.");
        var scratch = _dir.CreateSubdirectory("uninstall").FullName;
        var installDir = Directory.CreateDirectory(Path.Combine(scratch, "local", "Programs", "Dictum")).FullName;
        File.WriteAllBytes(Path.Combine(installDir, "Dictum.exe"), [0x4D, 0x5A]); // a stand-in: the script only checks that it is there
        var data = Directory.CreateDirectory(Path.Combine(scratch, "roaming", "Dictum")).FullName;
        File.WriteAllText(Path.Combine(data, "dictionary.txt"), "Kowalski\n");

        var (exit, output) = RunScript("uninstall.ps1", "", scratch); // neither -KeepData nor -RemoveData, under -NonInteractive
        Assert.True(exit == 0, output);
        Assert.False(File.Exists(Path.Combine(installDir, "Dictum.exe")), output);
        Assert.True(File.Exists(Path.Combine(data, "dictionary.txt")), output);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in _dir.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            _dir.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
