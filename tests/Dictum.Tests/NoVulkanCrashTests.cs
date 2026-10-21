using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.App;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.Windows;
using Whisper.net.LibraryLoader;

namespace Dictum.Tests;

/// <summary>
/// The launch crash of 21/10/2026 (docs\DECISIONS.md, "Launch crash without a working Vulkan driver"): on a PC where the
/// Vulkan runtime cannot load, Whisper.net loads ggml-base-whisper.dll a second time and ggml's static initialiser aborts
/// the process with GGML_ASSERT. These pin the fix (GGML_NO_BACKTRACE in the C runtime's environment, set first in Main),
/// the first-run probe honouring whisper.gpu = cpu, and the CPU order when the Vulkan loader does not load.
/// </summary>
public sealed class NoVulkanCrashTests
{
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint getenv(string name);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int _putenv_s(string name, string value);

    /// <summary>What ggml itself reads: C getenv in the Universal C Runtime, not the process environment block.</summary>
    private static string? CGetenv(string name) => getenv(name) is var p and not 0 ? Marshal.PtrToStringAnsi(p) : null;

    [Fact]
    public void Apply_sets_GGML_NO_BACKTRACE_where_the_C_runtimes_getenv_reads_it()
    {
        Assert.Equal(0, _putenv_s(GgmlEnvironment.NoBacktrace, "")); // an empty value removes it: start from "not set"
        Environment.SetEnvironmentVariable(GgmlEnvironment.NoBacktrace, null);
        Assert.Null(CGetenv(GgmlEnvironment.NoBacktrace));

        Assert.Equal(0, GgmlEnvironment.Apply());

        Assert.Equal("1", CGetenv(GgmlEnvironment.NoBacktrace));
        Assert.Equal("1", Environment.GetEnvironmentVariable(GgmlEnvironment.NoBacktrace)); // .NET readers agree
    }

    /// <summary>The Main method's body as source, comments removed.</summary>
    private static string MainBody()
    {
        var source = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src", "Dictum.Windows", "Program.cs"));
        var start = source.IndexOf("public static int Main(string[] args)", StringComparison.Ordinal);
        Assert.True(start >= 0, "Program.cs has no Main(string[] args)");
        var body = source[(source.IndexOf('{', start) + 1)..];
        return Regex.Replace(body, @"//[^\n]*", "");
    }

    [Fact]
    public void Apply_is_the_first_statement_of_Main_before_the_arguments_are_read()
    {
        var statements = MainBody().Split(';').Select(s => Regex.Replace(s, @"\s+", " ").Trim()).ToList();
        Assert.Matches(@"^(var \w+ = )?GgmlEnvironment\.Apply\(\)$", statements[0]);
        // The Vulkan loader check is wired before anything can set the runtime order.
        Assert.Equal("WhisperEngine.VulkanLoaderPresent = GgmlEnvironment.VulkanLoaderLoads", statements[1]);
        Assert.Contains("CliOptions.Parse(args)", statements[2]);
    }

    [Fact]
    public void The_first_run_probe_uses_the_whisper_gpu_that_settings_json_names()
    {
        Assert.Equal("cpu", ModelCoordinator.FirstRunProbeGpu(new AppSettings { Whisper = new WhisperSettings { Gpu = "cpu" } }));
        Assert.Equal("auto", ModelCoordinator.FirstRunProbeGpu(null));
        Assert.Equal("auto", ModelCoordinator.FirstRunProbeGpu(new AppSettings()));

        // Through FirstRun itself: the value reaches the runtime probe (a model override skips the pickers).
        Assert.Equal("cpu", ProbedOnFirstRun(new AppSettings { Whisper = new WhisperSettings { Gpu = "cpu" } }));
        Assert.Equal("auto", ProbedOnFirstRun(null));
    }

    private static string? ProbedOnFirstRun(AppSettings? seeded)
    {
        using var scratch = new Scratch();
        var log = NullLog.Instance;
        string? probed = null;
        var coordinator = new ModelCoordinator(scratch.Paths, new ModelManager(log), new LocalLlmManager(log), new HttpClient(new CountingHandler()), log,
            () => new AppSettings(), launchOverride: Path.Combine(scratch.Root.FullName, "model.bin"), new ScriptedPrompts(), _ => null,
            () => { }, () => { }, () => { }, (_, _) => { })
        {
            TestRuntimeProbe = (gpu, _) => { probed = gpu; return "CPU"; },
        };
        Assert.True(coordinator.FirstRun(seeded: seeded));
        return probed;
    }

    [Fact]
    public void Auto_starts_on_the_CPU_order_when_the_Vulkan_loader_does_not_load()
    {
        RuntimeLibrary[] cpu = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        RuntimeLibrary[] vulkanFirst = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        var (order, why) = WhisperEngine.RuntimeOrder("auto", () => false);
        Assert.Equal(cpu, order);
        Assert.Contains("vulkan-1.dll", why);

        Same(vulkanFirst, WhisperEngine.RuntimeOrder("auto", () => true));
        Same(vulkanFirst, WhisperEngine.RuntimeOrder("auto", null)); // no check (tests, another platform): as before
        Same(vulkanFirst, WhisperEngine.RuntimeOrder("vulkan", () => false)); // asked for: tried, whatever the loader does
        var asked = false;
        Same(cpu, WhisperEngine.RuntimeOrder("cpu", () => asked = true));
        Assert.False(asked); // cpu never looks for the loader
    }

    private static void Same(RuntimeLibrary[] expected, (RuntimeLibrary[] Order, string? Why) actual)
    {
        Assert.Equal(expected, actual.Order);
        Assert.Null(actual.Why);
    }

    [Fact]
    public void CI_runs_the_published_build_check_after_the_publish_step()
    {
        var ci = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), ".github", "workflows", "ci.yml"));
        var step = Regex.Match(ci, @"- name: Test the published build where the Vulkan runtime cannot load.*?(?=\n      - |\n\s*\n  \w)", RegexOptions.Singleline);
        Assert.True(step.Success, "ci.yml has no step named 'Test the published build where the Vulkan runtime cannot load'");
        Assert.Matches(@"(?m)^\s*DICTUM_PUBLISHED_DIR: \$\{\{ github\.workspace \}\}\\dist\\Dictum\s*$", step.Value);
        Assert.Contains("--filter \"FullyQualifiedName~Dictum.Tests.PublishedVulkanFailureTests\"", step.Value);
        Assert.True(ci.IndexOf("run: ./scripts/publish.ps1", StringComparison.Ordinal) < step.Index, "the check runs before dist\\Dictum is published");
    }
}

/// <summary>
/// Gated on DICTUM_PUBLISHED_DIR (dist\Dictum from publish.ps1): the real Dictum.exe, in a copy whose Vulkan runtime cannot
/// load because ggml-vulkan-whisper.dll's import of vulkan-1.dll is patched to a name no PC has, must not abort. Before the
/// fix of 21/10/2026 this exited 0xC0000409 with GGML_ASSERT(prev != ggml_uncaught_exception) on stderr. Runs --selftest only,
/// with APPDATA and LOCALAPPDATA in a temporary folder, never the user's Dictum folders.
/// </summary>
public sealed class PublishedVulkanFailureTests
{
    private const int StackBufferOverrun = unchecked((int)0xC0000409); // -1073740791, what ggml's abort exits with

    /// <param name="gpu">auto: what every user has. vulkan: Vulkan is tried whatever this PC's loader does, so the failing load
    /// (and with it the crash path) is exercised even on a PC without vulkan-1.dll.</param>
    [Theory]
    [InlineData("auto")]
    [InlineData("vulkan")]
    public async Task A_Vulkan_runtime_that_cannot_load_falls_back_to_the_CPU_instead_of_aborting(string gpu)
    {
        var layout = TestEnv.Get("PUBLISHED_DIR");
        Assert.SkipWhen(layout is null, @"DICTUM_PUBLISHED_DIR is not set (dist\Dictum from publish.ps1).");
        Assert.True(File.Exists(Path.Combine(layout, "Dictum.exe")), $"{layout} holds no Dictum.exe");

        var root = Directory.CreateTempSubdirectory("dictum-novulkan-");
        try
        {
            var app = Path.Combine(root.FullName, "app");
            CopyFolder(layout, app);
            PatchVulkanImport(Path.Combine(app, "runtimes", "vulkan", "win-x64", "ggml-vulkan-whisper.dll"));

            var roaming = Directory.CreateDirectory(Path.Combine(root.FullName, "roaming")).FullName;
            var local = Directory.CreateDirectory(Path.Combine(root.FullName, "local")).FullName;
            Directory.CreateDirectory(Path.Combine(roaming, "Dictum"));
            // F24 as the hotkey, so the self-test's 5-second key check cannot catch a real key; base.en, the model that comes
            // with Dictum (models\ beside Dictum.exe), so the self-test loads a real model on the runtime that loaded.
            File.WriteAllText(Path.Combine(roaming, "Dictum", "settings.json"),
                $$"""{ "hotkey": "F24", "whisper": { "model": "base.en", "gpu": "{{gpu}}" } }""");

            var psi = new ProcessStartInfo(Path.Combine(app, "Dictum.exe"), "--selftest")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = app,
            };
            psi.Environment["APPDATA"] = roaming;
            psi.Environment["LOCALAPPDATA"] = local;
            psi.Environment.Remove("DICTUM_MODEL_PATH");
            psi.Environment.Remove("DICTATE_MODEL_PATH");
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail("Dictum.exe --selftest did not finish in 3 minutes.");
                }
            }
            var (output, errors) = (await stdout, await stderr);
            var logs = Path.Combine(local, "Dictum", "logs");
            var log = Directory.Exists(logs) ? string.Join("\n", Directory.EnumerateFiles(logs).Select(File.ReadAllText)) : "";
            var report = $"exit {process.ExitCode}\n--- stdout\n{output}\n--- stderr\n{errors}\n--- log\n{log}";

            Assert.True(process.ExitCode != StackBufferOverrun, $"Dictum aborted (0xC0000409).\n{report}");
            Assert.False(errors.Contains("GGML_ASSERT", StringComparison.Ordinal), report);
            Assert.True(process.ExitCode is 0 or 1, report); // a normal return: 1 when another line (a microphone) fails
            Assert.True(Regex.IsMatch(output, @"(?m)^whisper runtime\s+CPU\b.*\[OK\]\s*$"), report);
            // The Vulkan runtime was tried first, so the CPU line above is the fallback after its failed load (the path that
            // aborted before the fix); or, for auto on a PC with no Vulkan loader, the CPU order was chosen and the log says why.
            if (gpu == "vulkan" || log.Contains("Whisper runtime order: Vulkan", StringComparison.Ordinal))
                Assert.True(log.Contains("Whisper runtime order: Vulkan -> Cpu -> CpuNoAvx", StringComparison.Ordinal), report);
            else
                Assert.True(log.Contains("vulkan-1.dll, does not load on this PC", StringComparison.Ordinal), report);
        }
        finally
        {
            try
            {
                root.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a file still held for a moment after the process ended: the temporary folder is left behind
            }
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>Renames the DLL's import of vulkan-1.dll to vulkan-X.dll (same length), so Windows cannot resolve it.</summary>
    private static void PatchVulkanImport(string dll)
    {
        var bytes = File.ReadAllBytes(dll);
        var name = Encoding.ASCII.GetBytes("vulkan-1.dll");
        var at = new List<int>();
        for (var i = bytes.AsSpan().IndexOf(name); i >= 0;)
        {
            at.Add(i);
            var next = bytes.AsSpan(i + 1).IndexOf(name);
            i = next < 0 ? -1 : i + 1 + next;
        }
        Assert.Single(at); // exactly one occurrence: the import, nothing else
        bytes[at[0] + "vulkan-".Length] = (byte)'X';
        File.WriteAllBytes(dll, bytes);
    }
}
