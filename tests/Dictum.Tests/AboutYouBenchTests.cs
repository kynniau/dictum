using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// About you bench (21/10/2026, DECISIONS): synthetic dictations naming Australian lenders and products and United Kingdom
/// insurers and products, spoken plainly, each cleaned three times by a real model (no About you, the Australian one, the
/// United Kingdom one) with the app's own prompt assembly (PersonalText.ForCleanup → PersonalCleanup.Checks) and validator.
/// Gated by DICTUM_CLEANUP_MODEL_PATH (a .gguf) and DICTUM_ABOUT_YOU_BENCH (a file the rows are appended to, tab-separated);
/// writes nothing else. A reply the validator accepts with a word of four letters or more that the model added only with
/// About you fails the run: the line may change spelling, never content. Each row also records what the app would paste
/// (the accepted reply, or the rule-based tidy) and how many of the expected spellings that has.
/// </summary>
[Collection("Process-wide state")] // loads a native model: never beside another model-loading class
public partial class AboutYouBenchTests(ITestOutputHelper output)
{
    public const string Australian = "I work for an Australian finance company dealing with Australian lenders, bank policy and terminology.";
    public const string British = "I am a financial planner in the United Kingdom.";

    /// <summary>Raw (as whisper writes plain speech), then each spelling the reply should have, as a case-sensitive pattern.</summary>
    public static readonly (string Raw, string[] Expected)[] Inputs =
    [
        ("i spoke to westpac about the offset account and they said the comparison rate is higher", ["Westpac", "offset account", "comparison rate"]),
        ("the client has a pre approval with nab and wants to avoid lmi on the new purchase", [@"\bNAB\b", @"\bLMI\b"]),
        ("anz will need two payslips before they assess the loan", [@"\bANZ\b"]),
        ("macquarie offers a cashback but their serviceability calculator is stricter", ["Macquarie"]),
        ("can you check whether commbank or bankwest has the better variable rate for this borrower", ["CommBank", "Bankwest"]),
        ("the broker lodged the application through ing and the valuation came back short", [@"\bING\b"]),
        ("my client wants to move her pension and her life cover is with aviva", ["Aviva"]),
        ("we compared legal and general against vitality for the critical illness policy", ["Legal (&|and) General", "Vitality"]),
        ("she should use her isa allowance before the end of the tax year", [@"\bISA\b"]),
        ("royal london and zurich both quoted for the income protection", ["Royal London", "Zurich"]),
        ("the fca expects us to record why the sipp suits the client", [@"\bFCA\b", @"\bSIPP\b"]),
        ("hargreaves lansdown holds his stocks and shares isa", ["Hargreaves Lansdown", @"\bISA\b"]),
        ("suncorp approved it yesterday", ["Suncorp"]),
        ("check the lvr with the lender", [@"\bLVR\b"]),
        ("send the scottish widows quote to the client", ["Scottish Widows"]),
        ("her pension is with standard life", ["Standard Life"]),
        ("um can you send me the report by friday", []), // controls: nothing to spell, nothing to add
        ("what is the comparison rate on that loan question mark", [@"\?"]),
    ];

    private static readonly (string Name, string? Text)[] Contexts = [("none", null), ("AU", Australian), ("UK", British)];

    private static readonly HashSet<string> Everyday = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "a", "an", "the", "for", "in", "with", "and", "am", "work", "dealing", "company",
    };

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordPattern();

    private static HashSet<string> WordsOf(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task Context_picks_spellings_and_never_adds_content()
    {
        var model = LocalLlmIntegrationTests.ModelPath;
        var file = TestEnv.Get("ABOUT_YOU_BENCH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(file), "DICTUM_CLEANUP_MODEL_PATH and DICTUM_ABOUT_YOU_BENCH are not both set.");
        var ct = TestContext.Current.CancellationToken;
        using var engine = LocalLlmEngine.Load(model!, TestEnv.Get("LLM_GPU") == "cpu" ? "cpu" : "auto", NullLog.Instance);
        var name = Path.GetFileNameWithoutExtension(model!);
        var rows = new StringBuilder();
        var leaks = new List<string>();
        var withoutContext = new Dictionary<string, HashSet<string>>(); // per input: the words the reply added with no About you
        foreach (var (contextName, contextText) in Contexts)
        {
            var personal = new PersonalText(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty, context: SpeakerContext.Of(contextText));
            var (prompt, validation) = personal.ForCleanup(new ProfanitySettings()).Checks(new LlmSettings(), _ => { });
            Assert.Equal(contextText is not null, prompt.Contains(CleanupPrompt.ContextLinePrefix, StringComparison.Ordinal));
            await engine.WarmUpAsync(prompt, ct);
            foreach (var (raw, expected) in Inputs)
            {
                var max = CleanupExamples.LocalMaxTokens(engine.CountTokens(raw));
                var sw = Stopwatch.StartNew();
                var result = await engine.CompleteAsync(prompt, CleanupExamples.Pairs, raw, max, ct);
                var ms = sw.ElapsedMilliseconds;
                var reply = result.Text.Trim();
                var (accepted, rejection) = result.Finished ? CleanupValidator.Validate(raw, result.Text, validation) : (null, $"reply truncated at {max} tokens");
                var hits = expected.Count(p => Regex.IsMatch(reply, p));
                var typed = accepted ?? DeterministicCleaner.Clean(raw); // what the app pastes: the reply, or the rule-based tidy
                var typedHits = expected.Count(p => Regex.IsMatch(typed, p));
                var added = WordsOf(reply).Except(WordsOf(raw)).OrderBy(w => w, StringComparer.Ordinal).ToList();
                // Content the About you line brought in: a word of four letters or more that the reply added only with it.
                List<string> brought = [];
                if (contextText is null) withoutContext[raw] = [.. added];
                else brought = added.Where(w => w.Length > 3 && !withoutContext[raw].Contains(w) && !Everyday.Contains(w)).ToList();
                // A content change the validator rejects is never typed (the tidy is); one it accepts is the blocker.
                if (brought.Count > 0 && accepted is not null) leaks.Add($"{contextName}: \"{raw}\" → \"{reply}\" (added {string.Join(", ", brought)})");
                var verdict = accepted is not null ? "accepted" : $"rejected: {rejection}";
                rows.Append(string.Join('\t', name, LocalLlmEngine.Backend, contextName, raw, $"{hits}/{expected.Length}", verdict, ms,
                    string.Join(' ', added), string.Join(' ', brought), reply.Replace('\n', '⏎'), $"{typedHits}/{expected.Length}")).Append('\n');
                output.WriteLine($"{contextName} {hits}/{expected.Length} {verdict} | {reply}");
            }
        }
        await File.AppendAllTextAsync(file!, rows.ToString(), ct);
        Assert.True(leaks.Count == 0, "About you changed content and the validator accepted it: " + string.Join("; ", leaks));
    }
}
