using System.Runtime.InteropServices;
using Dictum.Windows.Interop;

namespace Dictum.Windows;

/// <summary>
/// What whisper.cpp's native runtime (ggml) needs from the process before it first loads (docs\DECISIONS.md, "Launch crash
/// without a working Vulkan driver", 21/10/2026).
/// </summary>
internal static class GgmlEnvironment
{
    /// <summary>The variable that makes ggml skip installing its terminate handler (ggml.cpp's static initialiser).</summary>
    public const string NoBacktrace = "GGML_NO_BACKTRACE";

    /// <summary>
    /// Sets GGML_NO_BACKTRACE=1 in the C runtime's environment, which ggml reads with getenv, and in the process environment,
    /// so .NET readers agree. Whisper.net 1.9.1 loads ggml-base-whisper, ggml-cpu-whisper and ggml-vulkan-whisper one by one,
    /// frees the loaded ones when one fails, and tries the same runtimes folder again (its search list holds it twice), so
    /// ggml-base-whisper.dll is mapped a second time at the same address; its static initialiser then finds its own stale
    /// terminate handler as the previous one and GGML_ASSERT aborts the process (exit 0xC0000409) before the tray appears,
    /// on every PC where the Vulkan runtime cannot load. With the variable set the initialiser installs nothing and asserts
    /// nothing. Environment.SetEnvironmentVariable alone is not enough: in a .NET process the UCRT's environment copy already
    /// exists before Main, so SetEnvironmentVariable alone is never seen by ggml's getenv; _putenv_s is required (merge lens,
    /// 21/10/2026; docs\DECISIONS.md). Must run first in Main: _putenv_s is not thread-safe.
    /// </summary>
    /// <returns>0 when the C runtime took the value, else the errno _putenv_s returned (the caller logs it; never throws).</returns>
    // simplification: ggml's terminate chain (its handler that prints a backtrace, then calls the previous handler) is never
    // installed. Ceiling: an uncaught C++ exception in ggml ends the process without ggml's backtrace, which this ggml cannot
    // print on Windows anyway (its backtrace printer is "platform not supported" there, per the investigation's reading of the
    // ggml source; the shipped ggml-base DLLs carry no backtrace text, checked 21/10/2026), so nothing is lost. LLamaSharp's
    // ggml-base.dll reads the same variable, so the clean-up engine's ggml skips it too. Trigger to retire: Whisper.net no
    // longer freeing a ggml library and loading it again, or ggml dropping the assert.
    public static int Apply()
    {
        int result;
        try
        {
            result = Native._putenv_s(NoBacktrace, "1");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            result = -1; // no Universal C Runtime: then no whisper.cpp DLL could load either
        }
        Environment.SetEnvironmentVariable(NoBacktrace, "1");
        return result;
    }

    /// <summary>
    /// Whether the Vulkan loader (vulkan-1.dll, which ggml-vulkan-whisper.dll imports) loads in this process, looked up by
    /// its bare name. When it does not, the Vulkan runtime cannot load either, and trying it only
    /// costs time at every start (the investigation of 21/10/2026 measured about 9.4 s in Windows Sandbox), so whisper.gpu =
    /// auto starts on the CPU order (<see cref="Dictum.Transcribe.WhisperEngine.VulkanLoaderPresent"/>). The handle is freed
    /// again at once. The publish carries no vulkan-1.dll (checked 21/10/2026), so the copy either lookup finds is the
    /// system's (System32, from the GPU driver), or none.
    /// </summary>
    public static bool VulkanLoaderLoads()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
