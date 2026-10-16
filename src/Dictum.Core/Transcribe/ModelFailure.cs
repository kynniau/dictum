using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dictum.Transcribe;

/// <summary>
/// Plain words for a model that could not load, and what the user can do about it, shared by the speech and clean-up models
/// (<see cref="ModelManager"/>, <see cref="Llm.LocalLlmManager"/>), for the tray panel, Settings > About and the load balloon. The
/// Store certification investigation (16/10/2026) saw the engines' own words reach the user: Whisper.net's "Failed to load
/// native whisper library. Error: Cannot load the library on this platform using NativeLibrary. PInvokeError: The operation
/// completed successfully." and LLamaSharp's "The type initializer for 'LLama.Native.NativeApi' threw an exception.", in Windows
/// Sandbox without the Visual C++ runtime.
/// </summary>
public static class ModelFailure
{
    /// <summary>
    /// An engine's native library cannot load because the Visual C++ runtime it imports is not on this PC. PROPOSED copy
    /// (17/10/2026), awaiting the product owner's review: pinned word for word; do not reword.
    /// </summary>
    public const string RuntimeMissing = "The Microsoft Visual C++ runtime is missing; install it from Microsoft, then restart Dictum";

    /// <summary>
    /// The model failed on the GPU and was loaded again on the CPU, where it runs now. PROPOSED copy (17/10/2026), awaiting the
    /// product owner's review: pinned word for word; do not reword.
    /// </summary>
    public const string GpuFailed = "The graphics driver could not run the model; Dictum will use the CPU";

    /// <summary>
    /// The GPU ended Dictum while it loaded this model at an earlier start, so this start loads it on the CPU
    /// (<see cref="GpuLoadMarker"/>); true at every start while the marker stays. PROPOSED copy (17/10/2026, lens fix replacing
    /// "… could not run the model last time; Dictum is using the CPU", which was untrue from the second start on), awaiting the
    /// product owner's review: pinned word for word; do not reword.
    /// </summary>
    public const string GpuFailedLastTime = "The graphics driver could not run this model, so Dictum uses the CPU for it.";

    /// <summary>
    /// The Visual C++ runtime DLLs the engines import: all 54 whisper.cpp and llama.cpp DLLs under the build's runtimes folder
    /// import MSVCP140 and VCRUNTIME140, 38 also VCRUNTIME140_1 and 14 (ggml's CPU and base builds) VCOMP140 (their import
    /// tables, read 17/10/2026; the other 9 DLLs there are Avalonia's and import none).
    /// </summary>
    internal static readonly string[] RuntimeDlls = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll"];

    /// <summary>
    /// The oldest Visual C++ runtime the engines may run on: a copy of <c>$MinVcRuntime</c> in scripts/build-common.ps1 (14.51,
    /// on main since PR #10, 6daff77, read 17/10/2026), where Microsoft's rule is recorded: a runtime at least as recent as the
    /// build tools that built the app, and the newest linker among the native DLLs is 14.51 (Whisper.net.Runtime.NoAvx 1.9.1).
    /// A test holds the two equal. An msvcp140.dll older than this counts as missing (<see cref="VisualCppRuntimePresent()"/>).
    /// </summary>
    internal static readonly Version MinRuntime = new(14, 51);

    /// <summary>
    /// True when an engine's native library could not load at all, so no retry in this process can help: Whisper.net's "Failed to
    /// load native whisper library" (a string in Whisper.net 1.9.1, read 17/10/2026), LLamaSharp's failed NativeApi type
    /// initialiser or its own native-library errors (strings in LLamaSharp 0.27.0, read 17/10/2026), or a DLL not found.
    /// </summary>
    public static bool IsNativeLibraryFailure(Exception error) => Chain(error).Any(e =>
        e is DllNotFoundException
        || e is TypeInitializationException { TypeName: { } type } && type.StartsWith("LLama.Native.", StringComparison.Ordinal)
        || e.Message.StartsWith("Failed to load native whisper library", StringComparison.Ordinal)
        || e.Message.StartsWith("The native library cannot be correctly loaded", StringComparison.Ordinal)
        || e.Message.StartsWith("Failed to load the native library", StringComparison.Ordinal));

    /// <summary>
    /// What the user is told about a failed load (<see cref="Assessment.LastError"/>): plain words when the cause is known, else
    /// the engine's own message, as before.
    /// </summary>
    /// <param name="runtimePresent">Whether the Visual C++ runtime is on this PC; asked only after a native library failed to load.</param>
    public static string Describe(Exception error, Func<bool> runtimePresent) =>
        IsNativeLibraryFailure(error) && !runtimePresent() ? RuntimeMissing : error.GetBaseException().Message;

    /// <summary>
    /// Whether a load or warm-up that failed is worth one more try on the CPU: not when an engine library could not load (the CPU
    /// build needs the same runtime), the file is missing or fails its catalogue check, or the load was stopped.
    /// </summary>
    public static bool WorthCpuRetry(Exception error) =>
        !IsNativeLibraryFailure(error)
        && !Chain(error).Any(e => e is OperationCanceledException or FileNotFoundException or ModelIntegrityException or ObjectDisposedException);

    /// <summary>
    /// The managers' retry rule: a load or warm-up that failed while the GPU was in use (<paramref name="gpuInUse"/>: whisper's
    /// Vulkan runtime, llama.cpp's Vulkan build) is tried once more on the CPU when the CPU might get past it
    /// (<see cref="WorthCpuRetry"/>), unless Dictum is quitting.
    /// </summary>
    internal static bool RetryOnCpu(Exception error, bool gpuInUse, CancellationToken quitting) =>
        gpuInUse && WorthCpuRetry(error) && !quitting.IsCancellationRequested;

    /// <summary>Marks the failure of the retry on the CPU that followed a failure on the GPU (<see cref="Assess"/> reads it).</summary>
    internal static void CpuTried(Exception error) => error.Data[CpuTriedKey] = true;

    private const string CpuTriedKey = "Dictum.CpuTried";

    /// <summary>What a failed load leaves for the user (<see cref="Assess"/>).</summary>
    /// <param name="LastError">Why it failed, in plain words when the cause is known (<see cref="Describe"/>).</param>
    /// <param name="CpuMightHelp">"Try on the CPU" is offered in the tray panel and About: see <see cref="Assess"/>.</param>
    /// <param name="RestartForCpu">It failed on the CPU inside a GPU runtime with its gpu setting at cpu: only a restart loads the
    /// CPU-only runtime, and the problem line says so.</param>
    public sealed record Assessment(string LastError, bool CpuMightHelp, bool RestartForCpu);

    /// <summary>
    /// The one rule for both models. Try on the CPU (<see cref="Assessment.CpuMightHelp"/>) is offered for a failure inside a GPU
    /// runtime that the CPU might get past (<see cref="WorthCpuRetry"/>), with the gpu setting not cpu, and only when no retry on
    /// the CPU ran for it: once that retry has failed, the CPU's own failure is the reason, and the button would repeat the same
    /// attempt and write gpu = cpu for good (the lens's real run R1, 17/10/2026: a truncated base.en failed on the GPU and on the
    /// CPU, and the button was offered). The case left is a start that loaded the model on the CPU inside a GPU runtime because
    /// the GPU ended Dictum last time (<see cref="GpuLoadMarker"/>), where the CPU-only runtime has not been tried.
    /// </summary>
    /// <param name="gpuInUse">A GPU runtime was in use when the load failed.</param>
    /// <param name="gpuSetting">The model's gpu setting (whisper.gpu or llm.gpu) as the user set it.</param>
    /// <param name="runtimePresent">Whether the Visual C++ runtime is on this PC (<see cref="Describe"/>).</param>
    public static Assessment Assess(Exception error, bool gpuInUse, string gpuSetting, Func<bool> runtimePresent)
    {
        var inGpuRuntime = gpuInUse && WorthCpuRetry(error);
        var cpuTried = Chain(error).Any(e => e.Data.Contains(CpuTriedKey));
        return new Assessment(Describe(error, runtimePresent), inGpuRuntime && gpuSetting != "cpu" && !cpuTried, inGpuRuntime && gpuSetting == "cpu");
    }

    /// <summary>
    /// The Visual C++ runtime counts as present when each of its DLLs loads by name (System32, the program's folder, a packaged
    /// app's framework dependency) or sits in one of the program's native runtime folders (a copy beside the engines), and an
    /// msvcp140.dll found is not older than <see cref="MinRuntime"/>. Always true off Windows, where there is no such runtime.
    /// </summary>
    public static bool VisualCppRuntimePresent() => VisualCppRuntimePresent(LoadedPath, AppLocalFolders, FileVersion, OperatingSystem.IsWindows());

    /// <param name="loadedPath">The file a DLL loads from by name, or null when it does not load.</param>
    /// <param name="appLocalFolders">The program's native runtime folders (asked for only on Windows).</param>
    /// <param name="fileVersion">A file's version, or null when it cannot be read (the file then counts, as before).</param>
    /// <param name="isWindows">The platform is Windows; off it the answer is true and nothing is probed.</param>
    internal static bool VisualCppRuntimePresent(Func<string, string?> loadedPath, Func<IReadOnlyCollection<string>> appLocalFolders, Func<string, Version?> fileVersion,
        bool isWindows) => !isWindows || VisualCppRuntimePresent(loadedPath, appLocalFolders(), fileVersion);

    /// <param name="loadedPath">The file a DLL loads from by name, or null when it does not load.</param>
    /// <param name="appLocalFolders">The program's native runtime folders.</param>
    /// <param name="fileVersion">A file's version, or null when it cannot be read (the file then counts, as before).</param>
    internal static bool VisualCppRuntimePresent(Func<string, string?> loadedPath, IReadOnlyCollection<string> appLocalFolders, Func<string, Version?> fileVersion) =>
        RuntimeDlls.All(dll => Usable(dll, loadedPath(dll), fileVersion)
                               || appLocalFolders.Select(folder => Path.Combine(folder, dll)).Any(copy => File.Exists(copy) && Usable(dll, copy, fileVersion)));

    private static bool Usable(string dll, string? path, Func<string, Version?> fileVersion) =>
        path is not null && (dll != "msvcp140.dll" || fileVersion(path) is not { } version || version >= MinRuntime);

    /// <summary>The file a DLL loads from by name (the module this process has under that name), or null when it does not load.</summary>
    private static string? LoadedPath(string dll)
    {
        if (!NativeLibrary.TryLoad(dll, out var handle)) return null;
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, dll, StringComparison.OrdinalIgnoreCase))?.FileName ?? dll;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return dll; // it loads; where from is not known, so its version is not checked
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>A file's major and minor version from its version resource, or null when it has none or cannot be read.</summary>
    private static Version? FileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart == 0 && info.FileMinorPart == 0 ? null : new Version(info.FileMajorPart, info.FileMinorPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string[] AppLocalFolders()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        try
        {
            return Directory.Exists(runtimes) ? Directory.GetDirectories(runtimes, "*", SearchOption.AllDirectories) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The exception and every exception inside it (inner exceptions, and each of an aggregate's).</summary>
    private static IEnumerable<Exception> Chain(Exception error)
    {
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (e.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
