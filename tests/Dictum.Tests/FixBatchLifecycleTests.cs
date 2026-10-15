using System.Diagnostics;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.Platform;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>Fix batch, items 11, 21 and 22 (#22, #34, #55): quit, the single-instance lock, the integrity message and integrated GPUs.</summary>
public sealed class FixBatchLifecycleTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-fixc2-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    /// <summary>Hands out its token, then works for up to 5 s unless cancelled.</summary>
    private sealed class TokenTranscriber : ITranscriber
    {
        public TaskCompletionSource<CancellationToken> Token { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            Token.TrySetResult(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return "hello";
        }
    }

    [Fact]
    public async Task Quit_cancels_a_dictation_in_progress()
    {
        var transcriber = new TokenTranscriber();
        var settings = new AppSettings();
        var cleaner = TranscriptCleaner.RulesOnly("test");
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(transcriber), () => cleaner, (_, _) => Task.CompletedTask, () => settings, NullLog.Instance);
        var controller = new DictationController(new DictationStateMachine(StateMachineOptions.Default), new FakeCapture(), pipeline, new FakeUi(), () => settings, NullLog.Instance);
        var t0 = Environment.TickCount64;
        controller.Post(new HookEvent(HotkeySignal.Down, t0));
        controller.Post(new HookEvent(HotkeySignal.Up, t0 + 500));
        var token = await transcriber.Token.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        controller.Dispose(); // tray > Quit
        Assert.True(await Eventually(() => token.IsCancellationRequested, 1000), "quit left the dictation running, so disposing the engines waits for it");
    }

    private static string Unique() => @"Local\Dictum.Test." + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Another copy of Dictum: takes the lock on its own thread (a mutex belongs to the thread that took it), may create
    /// its "already running" event, and lets go when told, or never (abandon: its thread ends still owning the lock).
    /// </summary>
    private sealed class OtherCopy : IDisposable
    {
        private readonly ManualResetEventSlim _letGo = new();
        private readonly Thread _thread;
        private Mutex? _mutex;

        public OtherCopy(string mutexName, string? eventName, bool abandon = false)
        {
            using var taken = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _mutex = new Mutex(initiallyOwned: true, mutexName, out _);
                if (eventName is not null) Running = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                taken.Set();
                if (abandon) return;
                _letGo.Wait();
                _mutex.ReleaseMutex();
            }) { IsBackground = true };
            _thread.Start();
            taken.Wait();
        }

        public EventWaitHandle? Running { get; private set; }

        public void LetGo() => _letGo.Set();

        public void Dispose()
        {
            _letGo.Set();
            _thread.Join(2000);
            Running?.Dispose();
            _mutex?.Dispose();
        }
    }

    [Fact]
    public void A_second_launch_signals_the_running_copy()
    {
        var (mutex, signal) = (Unique(), Unique());
        using var other = new OtherCopy(mutex, signal);
        var instance = SingleInstance.Acquire(mutex, signal, TimeSpan.FromSeconds(5));
        var outcome = instance.Outcome;
        using (instance)
        {
            Assert.Equal(InstanceOutcome.SignalledRunningCopy, outcome);
            Assert.False(instance.Owned);
        }
        Assert.True(other.Running!.WaitOne(0));
    }

    [Fact]
    public void A_launch_takes_over_from_a_copy_that_is_quitting()
    {
        var (mutex, signal) = (Unique(), Unique());
        using var other = new OtherCopy(mutex, eventName: null); // quitting: its event is already closed
        _ = Task.Delay(300, Ct).ContinueWith(_ => other.LetGo(), TaskScheduler.Default);
        var sw = Stopwatch.StartNew();
        var instance = SingleInstance.Acquire(mutex, signal, TimeSpan.FromSeconds(5));
        var outcome = instance.Outcome;
        using (instance)
        {
            Assert.Equal(InstanceOutcome.Owned, outcome);
            Assert.True(instance.Owned);
        }
        Assert.InRange(sw.ElapsedMilliseconds, 200, 4000);
    }

    [Fact]
    public void An_abandoned_lock_counts_as_taken()
    {
        var (mutex, signal) = (Unique(), Unique());
        using var other = new OtherCopy(mutex, eventName: null, abandon: true); // the old copy ended without letting go
        var instance = SingleInstance.Acquire(mutex, signal, TimeSpan.FromSeconds(5));
        var outcome = instance.Outcome;
        using (instance)
        {
            Assert.Equal(InstanceOutcome.Owned, outcome);
            Assert.True(instance.Owned);
        }
    }

    [Fact]
    public void A_copy_that_never_lets_go_is_waited_for_a_bounded_time()
    {
        var (mutex, signal) = (Unique(), Unique());
        using var other = new OtherCopy(mutex, eventName: null);
        var sw = Stopwatch.StartNew();
        var instance = SingleInstance.Acquire(mutex, signal, TimeSpan.FromSeconds(1));
        var outcome = instance.Outcome;
        using (instance)
        {
            Assert.Equal(InstanceOutcome.TimedOut, outcome);
            Assert.False(instance.Owned);
        }
        Assert.InRange(sw.ElapsedMilliseconds, 900, 4000);
    }

    [Fact]
    public void A_picked_file_outside_the_models_folder_is_never_to_be_deleted()
    {
        var (models, downloads) = (_dir.CreateSubdirectory("models").FullName, _dir.CreateSubdirectory("downloads").FullName);
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance, () => models);
        var picked = Path.Combine(downloads, "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf"); // a community file sharing the catalogue name
        File.WriteAllBytes(picked, new byte[4096]);

        var outside = Assert.Throws<ModelIntegrityException>(() => integrity.Verify(picked)).Message;
        Assert.DoesNotContain("delete", outside, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rename it", outside, StringComparison.Ordinal);

        var own = Path.Combine(models, "qwen2.5-1.5b-instruct-q4_k_m.gguf");
        File.WriteAllBytes(own, new byte[4096]);
        Assert.Contains("Delete it and download it again", Assert.Throws<ModelIntegrityException>(() => integrity.Verify(own)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_same_size_file_with_another_hash_outside_the_models_folder_is_never_to_be_deleted()
    {
        var (models, downloads) = (_dir.CreateSubdirectory("models").FullName, _dir.CreateSubdirectory("downloads").FullName);
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance, () => models);
        var picked = Path.Combine(downloads, "ggml-tiny.en.bin"); // the smallest catalogue file; all zeros, so its SHA-256 differs
        using (var file = File.Create(picked)) file.SetLength(ModelCatalog.Find("tiny.en")!.Bytes);

        var outside = Assert.Throws<ModelIntegrityException>(() => integrity.Verify(picked)).Message;
        Assert.Contains("SHA-256", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("delete", outside, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rename it", outside, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ggml_vulkan: 0 = Quadro RTX 4000 (NVIDIA) | uma: 0 | fp16: 1 | bf16: 1 | warp size: 32 | shared memory: 49152 | int dot: 1 | matrix cores: NV_coopmat2", 0, false)]
    [InlineData("ggml_vulkan: 1 = Intel(R) UHD Graphics P630 (Intel Corporation) | uma: 1 | fp16: 1 | bf16: 0 | warp size: 32 | shared memory: 32768 | int dot: 0 | matrix cores: none", 1, true)]
    public void Vulkan_device_lines_say_which_GPUs_are_integrated(string line, int index, bool integrated) =>
        Assert.Equal((index, integrated), WhisperEngine.ParseDevice(line)); // lines as this PC's own log has them (read 15/10/2026)

    [Theory]
    [InlineData("ggml_vulkan: Found 2 Vulkan devices:")]
    public void Other_log_lines_are_not_devices(string line) => Assert.Null(WhisperEngine.ParseDevice(line));

    [Theory]
    [InlineData(2, 1, 1, true)] // this PC: a Quadro and the Intel iGPU
    [InlineData(1, 0, 1, false)] // a laptop with integrated graphics only: CPU recommendations
    [InlineData(1, 1, 0, true)]
    [InlineData(0, 0, 0, false)] // Vulkan loaded, no device
    [InlineData(-1, 0, 0, true)] // devices never described: as before this batch
    public void Only_a_discrete_GPU_gets_the_GPU_recommendations(int found, int discrete, int integrated, bool gpu) =>
        Assert.Equal(gpu, WhisperEngine.IsGpuMachine(found, discrete, integrated));

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
