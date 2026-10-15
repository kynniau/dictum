using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Abstractions;
using LLama.Batched;
using LLama.Native;
using LLama.Sampling;

namespace Dictum.Llm;

/// <summary>What one generation did, for the reply and for the speed estimate behind the long-dictation skip.</summary>
public sealed record GenerationResult(string Text, bool Finished, int PromptTokens, TimeSpan PromptTime, int GeneratedTokens, TimeSpan GenerationTime);

/// <summary>
/// Keeps one llama.cpp context whose KV cache already holds the cleanup prefix (system prompt and the few-shot
/// examples, through the model's own chat template), so each dictation evaluates only its own tokens and the reply.
/// Uses LLamaSharp 0.27.0's <see cref="BatchedExecutor"/> with a single <see cref="Conversation"/>: the prefix is
/// prompted once, every call prompts the rest, and <see cref="ConversationExtensions.Rewind"/> then removes the
/// call's tokens from the KV cache, back to the end of the prefix. One sequence id is reused for the life of the
/// model (forking would allocate a new id per call). Not thread-safe: the engine serialises calls.
/// </summary>
internal sealed class CleanupSession(LLamaWeights weights, IContextParams contextParams) : IDisposable
{
    public static readonly string[] StopTexts = ["<|im_end|>", "<|endoftext|>", "<end_of_turn>", "<|eot_id|>"]; // ChatML (Qwen, SmolLM2), Gemma, Llama

    private BatchedExecutor? _executor;
    private Conversation? _conversation;
    private string? _prefixText;
    private int _prefixTokens;

    /// <summary>Tokens and time of the last prefix evaluation (at load and whenever the prompt changes).</summary>
    public (int Tokens, TimeSpan Time) LastPrefix { get; private set; }

    /// <summary>The counts of the latest call that was cancelled (no text), for the speed estimate.</summary>
    public GenerationResult? Cancelled { get; private set; }

    /// <summary>The prompt text up to the dictation, and the whole prompt with the dictation and the assistant start.</summary>
    public (string Prefix, string Full) Render(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw)
    {
        var template = new LLamaTemplate(weights);
        template.Add("system", systemPrompt);
        foreach (var (exampleRaw, exampleClean) in examples)
        {
            template.Add("user", exampleRaw);
            template.Add("assistant", exampleClean);
        }
        var prefix = Encoding.UTF8.GetString(template.Apply());
        template.Add("user", raw);
        template.AddAssistant = true;
        return (prefix, Encoding.UTF8.GetString(template.Apply()));
    }

    /// <summary>Evaluates the prefix into the KV cache unless it is already there.</summary>
    public async Task EnsurePrefixAsync(string prefix)
    {
        if (_conversation is not null && _prefixText == prefix) return;
        Reset();
        var sw = Stopwatch.StartNew();
        _executor = new BatchedExecutor(weights, contextParams);
        _conversation = _executor.Create();
        _conversation.Prompt(_executor.Context.Tokenize(prefix, addBos: true, special: true));
        await InferAllAsync().ConfigureAwait(false);
        _prefixTokens = _conversation.TokenCount;
        _prefixText = prefix;
        LastPrefix = (_prefixTokens, sw.Elapsed);
    }

    public async Task<GenerationResult> GenerateAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens,
        CancellationToken cancellationToken)
    {
        var (prefix, full) = Render(systemPrompt, examples, raw);
        await EnsurePrefixAsync(prefix).ConfigureAwait(false);
        var conversation = _conversation!;
        var executor = _executor!;
        // A template whose full prompt does not start with the prefix text cannot reuse the cache: rewind to empty,
        // and rebuild the prefix afterwards.
        var cached = full.StartsWith(prefix, StringComparison.Ordinal);
        var sw = Stopwatch.StartNew();
        var (promptTokens, promptTime, generated) = (0, TimeSpan.Zero, 0);
        try
        {
            if (!cached) conversation.Rewind(conversation.TokenCount);
            var rest = cached ? full[prefix.Length..] : full;

            var tokens = executor.Context.Tokenize(rest, addBos: !cached, special: true);
            conversation.Prompt(tokens);
            await InferAllAsync().ConfigureAwait(false);
            (promptTokens, promptTime) = (tokens.Length, sw.Elapsed);

            sw.Restart();
            var vocab = executor.Context.NativeHandle.ModelHandle.Vocab;
            using var sampler = new GreedySamplingPipeline();
            var decoder = new StreamingTokenDecoder(executor.Context);
            var text = new StringBuilder();
            var finished = false;
            while (generated < maxTokens)
            {
                cancellationToken.ThrowIfCancellationRequested(); // a cancelled reply never looks finished
                var token = conversation.Sample(sampler);
                if (token.IsEndOfGeneration(vocab))
                {
                    finished = true;
                    break;
                }
                decoder.Add(token);
                text.Append(decoder.Read());
                generated++;
                if (StopTexts.Any(s => text.ToString().Contains(s, StringComparison.Ordinal)))
                {
                    finished = true;
                    break;
                }
                cancellationToken.ThrowIfCancellationRequested(); // checked before prompting, so the cache can always be rewound
                conversation.Prompt(token);
                await InferAllAsync().ConfigureAwait(false);
            }
            var reply = text.ToString();
            foreach (var stop in StopTexts) reply = reply.Replace(stop, "", StringComparison.Ordinal);
            // Spaces only at the end: a dictated closing "new line" is a line break the validator keeps.
            return new GenerationResult(reply.TrimStart().TrimEnd(' ', '\t', '\r'), finished, promptTokens, promptTime, generated, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // What the cancelled call measured still counts for the speed estimate (a CPU that keeps hitting the deadline
            // would otherwise never get one).
            Cancelled = new GenerationResult("", false, promptTokens, promptTime, generated, sw.Elapsed);
            throw;
        }
        finally
        {
            if (cached) RewindToPrefix();
            else Reset();
        }
    }

    private void RewindToPrefix()
    {
        try
        {
            if (_conversation is { RequiresInference: false } c && _prefixText is not null && c.TokenCount >= _prefixTokens)
            {
                c.Rewind(c.TokenCount - _prefixTokens);
                if (c.TokenCount == _prefixTokens) return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // fall through: rebuild the cache on the next call
        }
        Reset();
    }

    private async Task InferAllAsync()
    {
        // Infer runs one batch per call; a prompt longer than the batch size is queued as several. The user's cancel is
        // not passed in: a decode interrupted half-way would leave the conversation unusable.
        while (_conversation!.RequiresInference)
        {
            var result = await _executor!.Infer().ConfigureAwait(false);
            if (result != DecodeResult.Ok) throw new InvalidOperationException($"llama.cpp decode failed: {result}");
        }
    }

    private void Reset()
    {
        _conversation?.Dispose();
        _executor?.Dispose();
        _conversation = null;
        _executor = null;
        _prefixText = null;
        _prefixTokens = 0;
    }

    public void Dispose() => Reset();
}
