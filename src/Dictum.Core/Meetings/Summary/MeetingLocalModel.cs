using Dictum.Llm;

namespace Dictum.Meetings.Summaries;

/// <summary>The manager seam keeps loading tests free of native model libraries.</summary>
internal sealed record MeetingLocalModel(Action<string, string> BeginLoad, Func<bool> IsLoading,
    Func<ILocalLlmEngine?> Ready)
{
    public static MeetingLocalModel For(LocalLlmManager manager) => new(manager.BeginLoad, () => manager.IsLoading, () => manager.Ready);

    public async Task<ILlmClient> Load(string? installedPath, string gpu, string missingMessage, string failedMessage,
        CancellationToken cancellation, TimeSpan? timeout = null)
    {
        if (installedPath is null) throw new FileNotFoundException(missingMessage);
        cancellation.ThrowIfCancellationRequested();
        BeginLoad(installedPath, gpu);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            while (IsLoading()) await Task.Delay(50, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(failedMessage);
        }
        var loaded = Ready() ?? throw new InvalidOperationException(failedMessage);
        return new SummaryClient(loaded, Path.GetFileName(installedPath));
    }

    private sealed class SummaryClient(ILocalLlmEngine engine, string model) : ILlmClient
    {
        public string Name => "local/" + model;
        public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
        {
            // Summary requests have their own prompt and no dictation clean-up examples.
            var result = await engine.CompleteAsync(systemPrompt, [], rawTranscript, 1024, cancellationToken);
            return new(result.Text, result.Finished ? "end_turn" : "max_tokens");
        }
    }
}
