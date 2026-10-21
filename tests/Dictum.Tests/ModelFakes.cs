using System.Collections.Concurrent;
using System.Net;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.App;

namespace Dictum.Tests;

/// <summary>A loaded speech model stand-in: records decoder changes and disposal; a transcription can wait for its token (up to 5 s).</summary>
internal sealed class FakeSpeechEngine(string path, DecodeOptions decode, bool waitForCancel = false) : ISpeechEngine
{
    private int _disposed;

    public string ModelPath { get; } = path;
    public DecodeOptions Decode { get; private set; } = decode;
    public bool Disposed => Volatile.Read(ref _disposed) == 1;

    public void Reconfigure(DecodeOptions options) => Decode = options;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (waitForCancel) await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return Path.GetFileName(ModelPath);
    }

    public void Dispose() => Volatile.Write(ref _disposed, 1);
}

/// <summary>
/// A loaded cleanup model stand-in that echoes the dictation, or gives a set reply. With <c>waitForCancel</c> its warm-up never
/// ends on its own, only when its token is cancelled, so a test proves a cancellation by the warm-up ending rather than by a clock.
/// </summary>
internal sealed class FakeLocalEngine(string path, bool waitForCancel = false, string? reply = null) : ILocalLlmEngine
{
    private int _disposed;
    private readonly TaskCompletionSource _warmUpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _warmUpCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ModelPath { get; } = path;
    public bool Disposed => Volatile.Read(ref _disposed) == 1;
    /// <summary>True once the warm-up is waiting on its token.</summary>
    public bool WarmingUp => _warmUpStarted.Task.IsCompleted;
    /// <summary>Completes when the warm-up starts waiting on its token.</summary>
    public Task WarmUpStarted => _warmUpStarted.Task;
    /// <summary>Completes when the warm-up's token is cancelled, before the cancellation leaves <see cref="WarmUpAsync"/>.</summary>
    public Task WarmUpCancelled => _warmUpCancelled.Task;
    public (double Prompt, double Generation) Rates => (0, 0);

    public async Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        if (waitForCancel)
        {
            _warmUpStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _warmUpCancelled.TrySetResult();
                throw;
            }
        }
        return TimeSpan.Zero;
    }

    public int CountTokens(string text) => text.Length / 4;

    public TimeSpan? Predict(int dictationTokens) => null;

    public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
        CancellationToken cancellationToken) => Task.FromResult(new GenerationResult(reply ?? raw, true, 0, TimeSpan.Zero, 0, TimeSpan.Zero));

    public void Dispose() => Volatile.Write(ref _disposed, 1);
}

/// <summary>Model loads that each wait until the test releases them, so loads can finish in any order.</summary>
internal sealed class GatedLoads
{
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new();

    public ConcurrentQueue<string> Started { get; } = new();
    public ConcurrentDictionary<string, FakeSpeechEngine> Speech { get; } = new();
    public ConcurrentDictionary<string, FakeLocalEngine> Cleanup { get; } = new();

    public void Release(string path) => Gate(path).Set();

    private ManualResetEventSlim Gate(string path) => _gates.GetOrAdd(path, _ => new ManualResetEventSlim());

    private void Wait(string path)
    {
        Started.Enqueue(path);
        if (!Gate(path).Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException($"{path} was never released");
    }

    public ISpeechEngine LoadSpeech(string path, WhisperSettings settings, DecodeOptions decode)
    {
        Wait(path);
        return Speech[path] = new FakeSpeechEngine(path, decode);
    }

    public ILocalLlmEngine LoadCleanup(string path, string gpu)
    {
        Wait(path);
        return Cleanup[path] = new FakeLocalEngine(path);
    }
}

/// <summary>An in-memory download server: each file's request waits until released (then serves the payload), cancelled, or 10 s (then 403).</summary>
internal sealed class GateHandler(byte[] payload) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private int _inFlight;
    private int _cancelled;

    public ConcurrentQueue<string> Requests { get; } = new();
    public int InFlight => Volatile.Read(ref _inFlight);
    public int Cancelled => Volatile.Read(ref _cancelled);

    public void Release(string fileName) => Gate(fileName).TrySetResult();

    private TaskCompletionSource Gate(string name) => _gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.RequestUri!.LocalPath);
        Requests.Enqueue(name);
        Interlocked.Increment(ref _inFlight);
        try
        {
            await Gate(name).Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }
        catch (TimeoutException)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>Model dialogs answered by the test.</summary>
internal sealed class ScriptedPrompts : IModelPrompts
{
    public ModelPick Speech { get; set; } = new(null, null, true);
    public CleanupPick Cleanup { get; set; } = new(null, null, false, true);
    public bool Confirm { get; set; }
    public int SpeechPickerCalls { get; private set; }
    public int CleanupPickerCalls { get; private set; }
    public int ConfirmCalls { get; private set; }
    public string? LastNote { get; private set; }

    public ModelPick FirstRunPicker(string recommended, string reason, Func<ModelInfo, bool> isDownloaded, string modelsDir)
    {
        SpeechPickerCalls++;
        return Speech;
    }

    public CleanupPick CleanupPicker(string recommended, string reason, Func<CleanupModelInfo, bool> isDownloaded, string modelsDir)
    {
        CleanupPickerCalls++;
        return Cleanup;
    }

    public MissingModelChoice MissingModel(string shortName, bool canDownload) => MissingModelChoice.Quit;

    /// <summary>The file names each plain download question named, in order.</summary>
    public List<string> ConfirmFiles { get; } = [];

    public bool ConfirmDownload(string fileName, string sizeText, string note)
    {
        ConfirmCalls++;
        ConfirmFiles.Add(fileName);
        LastNote = note;
        return Confirm;
    }

    /// <summary>The answer to the download question asked while another download runs (Cancel, until a test says otherwise).</summary>
    public BusyDownloadChoice Busy { get; set; } = BusyDownloadChoice.Cancel;

    /// <summary>Runs while the busy question is being asked, before it is answered.</summary>
    public Action? WhenBusyAsked { get; set; }

    /// <summary>Each busy question's text and buttons, as the window would show them.</summary>
    public List<(string Text, string[] Buttons)> BusyQuestions { get; } = [];

    public BusyDownloadChoice ConfirmDownloadWhileBusy(string fileName, string sizeText, string note, string chosen, string running, string runningProgress, bool canWait = true)
    {
        BusyQuestions.Add((ModelPromptText.BusyText(fileName, sizeText, note, chosen, running, runningProgress, canWait), ModelPromptText.BusyChoices(chosen, canWait)));
        LastNote = note;
        WhenBusyAsked?.Invoke(); // while the question is open: a test looks at the running download here
        return Busy;
    }

    /// <summary>The words of every plain (Simple view) question, as the window would show them.</summary>
    public List<string> PlainQuestions { get; } = [];

    /// <summary>Records the plain question's words, then answers as the interface's default does (through <see cref="ConfirmDownload"/>).</summary>
    public bool ConfirmPlainDownload(string fileName, string plainName, string sizeText, string? warning)
    {
        PlainQuestions.Add(ModelPromptText.PlainConfirmText(plainName, sizeText, warning));
        return ConfirmDownload(fileName, sizeText, warning is null ? ModelPromptText.PlainCheck : $"{ModelPromptText.PlainCheck}. {warning}");
    }

    /// <summary>Records the plain busy question's words, then answers as the interface's default does (through <see cref="ConfirmDownloadWhileBusy"/>).</summary>
    public BusyDownloadChoice ConfirmPlainDownloadWhileBusy(string fileName, string plainName, string sizeText, string? warning, string running, string runningProgress, bool canWait = true)
    {
        PlainQuestions.Add(ModelPromptText.PlainBusyText(plainName, sizeText, warning, running, runningProgress, canWait));
        return ConfirmDownloadWhileBusy(fileName, sizeText, warning is null ? ModelPromptText.PlainCheck : $"{ModelPromptText.PlainCheck}. {warning}", plainName, running, runningProgress, canWait);
    }

    public string? ChooseFile(string initialDir, bool cleanupModel) => null;
}

