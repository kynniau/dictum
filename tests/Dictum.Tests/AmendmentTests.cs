using System.Collections.Concurrent;
using Dictum.Cli;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// The lens re-check's open items (16/10/2026) and the SPEC §22 amendments the polish lane carries: each test depends on
/// one rule alone, and each was observed failing with that rule reverted (docs/DECISIONS.md, Polish lane).
/// </summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class AmendmentTests
{
    private static string? Reject(string raw, string reply, ValidationContext? context = null) => CleanupValidator.Validate(raw, reply, context).Rejection;

    [Fact]
    public void Commentary_is_matched_as_whole_words_in_the_dictation_too()
    {
        // Substring matching would find "here is" inside the dictated "there is" and let the remark through.
        const string raw = "there is a problem with the invoice from the supplier this week";
        Assert.Contains("here is", Reject(raw, "There is a problem with the invoice from the supplier this week. Here is the fix.") ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(Reject(raw, "There is a problem with the invoice from the supplier this week."));
    }

    [Fact]
    public void A_reply_echoing_the_sent_prompt_is_rejected_by_the_echo_check_alone()
    {
        // Twelve words against ten dictated (1.2x, inside the 1.6x bound), every dictated content word kept, seven of the twelve
        // dictated: only the five words "keep the tone warm and" from the prompt actually sent give it away.
        const string prompt = "Always keep the tone warm and friendly for our clients.";
        const string raw = "tell the clients the meeting has moved to thursday please";
        const string reply = "Keep the tone warm and friendly, tell clients the meeting moved Thursday.";
        Assert.Equal(12, ValidatorText.Words(reply.ToLowerInvariant()).Count);
        Assert.Equal("reply repeats five or more words of the system prompt", Reject(raw, reply, new ValidationContext(prompt)));
        Assert.Null(Reject(raw, reply)); // the same reply without that prompt: nothing else objects
    }

    [Fact]
    public void Swear_words_are_protected_from_the_clean_up_unless_the_filter_removes_them()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Paths.ProfanityOverrideFile, "bloody\n");
        var personal = PersonalText.Load(scratch.Paths, _ => { });
        Assert.Contains("bloody", personal.WordsTheCleanupMustKeep(new ProfanitySettings { Mode = ProfanitySettings.Off })!);
        Assert.Contains("bloody", personal.WordsTheCleanupMustKeep(new ProfanitySettings { Mode = ProfanitySettings.Mask })!);
        Assert.Null(personal.WordsTheCleanupMustKeep(new ProfanitySettings { Mode = ProfanitySettings.Remove })); // dropping them is what remove asks for
    }

    [Fact]
    public void The_prompt_keeps_swear_words_and_names_the_filter_as_the_only_control()
    {
        const string sentence = "Keep every word the speaker said, including swear words; the app's own profanity setting is the only thing that changes them.";
        Assert.Contains(sentence, CleanupPrompt.Default, StringComparison.Ordinal);
        Assert.Contains(sentence, CleanupPrompt.Build(CleanupPrompt.Default, grammarAssist: true, dictionaryLine: "Known names and terms, keep their spelling: Kynni"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cleanup_that_times_out_cancels_the_provider_before_it_returns()
    {
        // Deterministic: the provider gives up with a TimeoutException at once while the deadline is a minute away, so no
        // timer can have cancelled its token. Only the timeout path's own Cancel() does; without it the token is still live.
        var tokens = new ConcurrentQueue<CancellationToken>();
        var giving = new FakeLlmClient(ct =>
        {
            tokens.Enqueue(ct);
            return Task.FromException<LlmReply>(new TimeoutException("provider timed out"));
        });
        var cleaner = new TranscriptCleaner(giving, CleanupPrompt.Default, TimeSpan.FromMinutes(1), NullLog.Instance, "test", deadline: _ => TimeSpan.FromMinutes(1));
        for (var i = 0; i < 20; i++)
        {
            var result = await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
            Assert.Contains("timed out", result.FallbackReason);
            Assert.True(tokens.Last().IsCancellationRequested, $"run {i}: the provider's token was still live when CleanAsync returned");
        }
    }

    [Fact]
    public void Every_cli_command_stops_on_an_unreadable_settings_file()
    {
        using var scratch = new Scratch("{ \"hotkey\": ");
        var config = ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { });
        Assert.True(config.ForcedOffline);
        Assert.True(CliCommands.Unreadable(config, scratch.Paths));
        File.WriteAllText(scratch.Paths.SettingsFile, "{ }");
        Assert.False(CliCommands.Unreadable(ConfigLoader.Load(scratch.Paths, new SecretRedactor(), _ => null, _ => false, _ => { }), scratch.Paths));
    }

    [Fact]
    public async Task Transcribe_exits_1_on_an_unreadable_settings_file()
    {
        var model = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(model), "DICTUM_MODEL_PATH is not set (the command must reach the point where it would transcribe).");
        var wav = WhisperIntegrationTests.SynthesiseWav("hello there");
        try
        {
            using var scratch = new Scratch("{ \"hotkey\": ");
            var (options, error) = CliOptions.Parse(["--transcribe", wav, "--model", model!, "--no-llm"]);
            Assert.Null(error);
            Assert.Equal(1, await CliCommands.TranscribeAsync(options!, scratch.Paths, NullLog.Instance, new SecretRedactor()));
            File.WriteAllText(scratch.Paths.SettingsFile, "{ }"); // readable: the same command transcribes
            Assert.Equal(0, await CliCommands.TranscribeAsync(options!, scratch.Paths, NullLog.Instance, new SecretRedactor()));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Theory]
    [InlineData("http://example.com/v1", false)]
    [InlineData("http://192.168.1.20:8080/v1", false)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    public void Plain_http_is_accepted_only_for_a_server_on_this_PC(string url, bool accepted)
    {
        Assert.Equal(accepted, SettingsLoader.BaseUrlProblem(url) is null);
        var llm = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = url, Model = "m" };
        Assert.Equal(accepted, CloudPolicy.InsecureEndpoint(llm) is null);
        // The backstop: even a value that reached the cleaner directly is refused there.
        var cleaner = TranscriptCleaner.Create(llm with { CloudConsent = new Uri(url).Host }, false, SecretStore.Empty, new HttpClient(new CountingHandler()), NullLog.Instance);
        Assert.Equal(accepted, cleaner.UsesModel);
    }

    [Fact]
    public void A_cloud_provider_runs_only_for_the_host_the_user_agreed_to()
    {
        var llm = new LlmSettings { Provider = LlmSettings.OpenAiCompatible, BaseUrl = "https://llm.example.com/v1", Model = "m" };
        var http = new HttpClient(new CountingHandler());
        Assert.False(TranscriptCleaner.Create(llm, false, SecretStore.Empty, http, NullLog.Instance).UsesModel); // no consent
        Assert.False(TranscriptCleaner.Create(llm with { CloudConsent = "other.example.com" }, false, SecretStore.Empty, http, NullLog.Instance).UsesModel);
        Assert.True(TranscriptCleaner.Create(llm with { CloudConsent = "llm.example.com" }, false, SecretStore.Empty, http, NullLog.Instance).UsesModel);

        var warnings = new List<string>();
        var repaired = EffectiveSettings.Cleanup(new AppSettings { Llm = llm }, anthropicKeyPresent: false, () => false, warnings.Add);
        Assert.Equal(LlmSettings.Local, repaired.Llm.Provider); // no consent on record (consent.json, read at load): the local model
        Assert.Contains(warnings, w => w.Contains("consent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(llm.Provider, EffectiveSettings.Cleanup(new AppSettings { Llm = llm with { CloudConsent = "llm.example.com" } }, false, () => false, _ => { }).Llm.Provider);
    }
}
