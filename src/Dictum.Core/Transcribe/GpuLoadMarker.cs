using Dictum.Core;
using Dictum.Storage;

namespace Dictum.Transcribe;

/// <summary>
/// A file naming the model whose GPU load is under way: <see cref="SpeechFile"/> or <see cref="CleanupFile"/> in the per-user local
/// data folder, beside verified-models.json (under MSIX, in the package's LocalCache). When the GPU refuses a model's memory,
/// whisper.cpp ends the program instead of reporting an error ("ggml_vulkan: Device memory allocation of size 147365888 failed …
/// ErrorOutOfDeviceMemory … GGML_ASSERT(buffer) failed", 17/10/2026, exit 0xC0000409), and so can llama.cpp: no catch or finally
/// runs then, so the file outlives the program. The next start that finds it naming the model it loads first loads that model on
/// the CPU for the whole run and says so (<see cref="ModelFailure.GpuFailedLastTime"/>); the file stays, so starts do not alternate
/// between the GPU and the CPU. A GPU load of another model replaces it, so a model it does not name tries the GPU again.
/// </summary>
/// <param name="file">The marker file; null for a manager with no local data folder (no marker is read or written).</param>
internal sealed class GpuLoadMarker(string? file, ILog log)
{
    public const string SpeechFile = "gpu-load-speech.txt";
    public const string CleanupFile = "gpu-load-cleanup.txt";

    private readonly object _lock = new();
    private bool _checked; // the first load has looked for the file
    private string? _cpuModel; // the model the file named at the first load: on the CPU for this run
    private bool _written; // this run wrote the file for a GPU load still under way
    private bool _closed; // quit: nothing is written any more

    /// <summary>
    /// Asked before every load. The first time only, reads the file: when it names <paramref name="model"/> and the gpu setting is
    /// not cpu, that model loads on the CPU for the rest of this run. True for each load of that model.
    /// </summary>
    public bool CpuForThisRun(string model, string gpuSetting)
    {
        lock (_lock)
        {
            if (!_checked)
            {
                _checked = true;
                if (gpuSetting != "cpu" && Read() is { } named && Same(named, model))
                {
                    _cpuModel = model;
                    log.Warn($"{Path.GetFileName(file)} names {Path.GetFileName(model)}: the GPU ended Dictum while it loaded that model, so it loads on the CPU this time.");
                }
            }
            return _cpuModel is { } cpu && Same(cpu, model);
        }
    }

    /// <summary>Just before a GPU load: the file names the model until <see cref="End"/>.</summary>
    public void Begin(string model)
    {
        lock (_lock)
        {
            if (file is null || _closed) return;
            try
            {
                File.WriteAllText(file, Path.GetFullPath(model));
                _written = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                log.Warn($"Could not write {file} ({ex.GetType().Name}); if the GPU ends Dictum during this load, the next start tries the GPU again.");
            }
        }
    }

    /// <summary>After a GPU load, whatever its outcome (in a finally): the file this run wrote goes.</summary>
    public void End()
    {
        lock (_lock)
        {
            if (_written) Delete();
        }
    }

    /// <summary>At quit: a GPU load still under way did not end Dictum, so the file this run wrote for it goes; one read at the first load stays.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            if (_written) Delete();
        }
    }

    private string? Read()
    {
        if (file is null) return null;
        try
        {
            var named = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            return named.Length > 0 ? named : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not read {file} ({ex.GetType().Name}); the GPU is tried as usual.");
            return null;
        }
    }

    private void Delete()
    {
        _written = false;
        try
        {
            File.Delete(file!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not delete {file} ({ex.GetType().Name}); a start that loads that model first will load it on the CPU.");
        }
    }

    private static bool Same(string a, string b)
    {
        try
        {
            return PathRegistry.SamePath(a, b);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // not a path: it names no model
        }
    }
}
