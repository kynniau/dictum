using System.Collections.Concurrent;
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
/// Only with the gpu setting <c>auto</c>: a hand-set <c>vulkan</c> always tries the GPU, and <c>cpu</c> never does.
/// <para>
/// The speech and clean-up markers share one gate per folder, held from <see cref="Begin"/> to <see cref="End"/>: GPU loads run
/// one after the other, so this run writes at most one file at a time; a file left by an earlier start can sit beside it (a
/// speech file read at start puts speech on the CPU, so no Begin replaces it, while the clean-up's GPU load writes its own), and
/// a file this run writes names the load under way. Start-up begins both loads at
/// once; without the gate the clean-up's file was written while its load still queued behind whisper's on
/// <see cref="NativeLoadGate"/>, so a crash in whisper's load left both files and put the clean-up model on the CPU too (lens on
/// PR #12, 17/10/2026). GPU loads therefore run one after the other, warm-up included. NativeLoadGate itself cannot hold the
/// marker: it is a lock around the native load call alone, inside the engines, and the clean-up's warm-up (where shaders
/// compile) runs after it, across awaits.
/// </para>
/// </summary>
/// <param name="file">The marker file; null for a manager with no local data folder (no marker is read or written).</param>
internal sealed class GpuLoadMarker(string? file, ILog log)
{
    public const string SpeechFile = "gpu-load-speech.txt";
    public const string CleanupFile = "gpu-load-cleanup.txt";

    /// <summary>One gate per marker folder (the app has one; tests each have their own), shared by both managers' markers.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim? _gate = file is null ? null : Gates.GetOrAdd(Path.GetFullPath(Path.GetDirectoryName(file) ?? file), static _ => new SemaphoreSlim(1, 1));
    private readonly object _lock = new();
    private bool _held; // this marker holds the gate for a GPU load still under way
    private bool _checked; // the first load has looked for the file
    private string? _cpuModel; // the model the file named at the first load: on the CPU for this run
    private bool _written; // this run wrote the file for a GPU load still under way
    private bool _closed; // quit: nothing is written any more

    /// <summary>
    /// Asked before every load. The first time only, reads the file: when it names <paramref name="model"/> and the gpu setting is
    /// auto, that model loads on the CPU for the rest of this run. True for each load of that model while the setting is auto; a
    /// hand-set vulkan tries the GPU.
    /// </summary>
    public bool CpuForThisRun(string model, string gpuSetting)
    {
        lock (_lock)
        {
            if (!_checked)
            {
                _checked = true;
                if (gpuSetting == "auto" && Read() is { } named && Same(named, model))
                {
                    _cpuModel = model;
                    log.Warn($"{Path.GetFileName(file)} names {Path.GetFileName(model)}: the GPU ended Dictum while it loaded that model, so it loads on the CPU this time.");
                }
            }
            return gpuSetting == "auto" && _cpuModel is { } cpu && Same(cpu, model);
        }
    }

    /// <summary>
    /// Just before a GPU load: waits for the other manager's GPU load to end, then the file names the model until <see cref="End"/>,
    /// which every Begin is paired with (in a finally).
    /// </summary>
    public void Begin(string model)
    {
        // simplification: the gate covers the whole GPU load and warm-up, so the two models' warm-ups no longer overlap at start-up
        // and a GPU load that hangs holds the other model's GPU load behind it (the native loads already queued on NativeLoadGate).
        // Ceiling: start-up's time to both models ready is the sum of the two GPU loads; speech may wait for the clean-up's warm-up,
        // and a hung clean-up warm-up blocks speech until it is superseded, unloaded or Dictum quits (a hang inside native code is not
        // cancellable). Trigger to retire: a measured start-up
        // that is too slow, or a hung load reported; then record the marker per load step instead of per load.
        _gate?.Wait(); // outside _lock, so Close at quit never waits for the other manager's load
        lock (_lock)
        {
            _held = _gate is not null;
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

    /// <summary>After a GPU load, whatever its outcome (in a finally): the file this run wrote goes, then the gate opens.</summary>
    public void End()
    {
        lock (_lock)
        {
            if (_written) Delete();
            if (!_held) return;
            _held = false;
            _gate!.Release();
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
