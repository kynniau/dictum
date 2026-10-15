using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>SPEC §16.1: the Mode presets are a pure function of the hardware.</summary>
public class ModePresetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Light_is_base_en_with_the_rule_based_tidy_on_any_hardware(bool gpu)
    {
        Assert.Equal(new PresetValues("base.en", "none", null), ModePresets.For(ModePreset.Light, gpu));
    }

    [Fact]
    public void Full_is_large_v3_turbo_on_a_GPU_and_small_en_on_a_CPU_both_with_qwen_1_5b()
    {
        Assert.Equal(new PresetValues("large-v3-turbo", "local", "qwen2.5-1.5b"), ModePresets.For(ModePreset.Full, gpuRuntime: true));
        Assert.Equal(new PresetValues("small.en", "local", "qwen2.5-1.5b"), ModePresets.For(ModePreset.Full, gpuRuntime: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModePresets.For(ModePreset.Custom, true));
    }

    private static AppSettings With(string speech, string provider, string cleanup = "qwen2.5-1.5b", int beam = 0) => new()
    {
        Whisper = new WhisperSettings { Model = speech, BeamSize = beam },
        Llm = new LlmSettings { Provider = provider, Model = cleanup },
    };

    [Fact]
    public void Settings_match_a_preset_or_custom()
    {
        Assert.Equal(ModePreset.Light, ModePresets.Match(With("base.en", "none"), gpuRuntime: true));
        Assert.Equal(ModePreset.Light, ModePresets.Match(With(@"C:\models\ggml-base.en.bin", "none", "anything"), gpuRuntime: false));
        Assert.Equal(ModePreset.Full, ModePresets.Match(With("small.en", "local"), gpuRuntime: false));
        Assert.Equal(ModePreset.Custom, ModePresets.Match(With("small.en", "local"), gpuRuntime: true)); // Full on a GPU is large-v3-turbo
        Assert.Equal(ModePreset.Full, ModePresets.Match(With("large-v3-turbo", "local", @"D:\m\qwen2.5-1.5b-instruct-q4_k_m.gguf", beam: 5), gpuRuntime: true));
        Assert.Equal(ModePreset.Custom, ModePresets.Match(With("large-v3-turbo", "local", "qwen2.5-0.5b"), gpuRuntime: true));
        Assert.Equal(ModePreset.Custom, ModePresets.Match(new AppSettings(), gpuRuntime: true)); // medium.en + local
    }

    [Fact]
    public void A_preset_writes_concrete_values()
    {
        Assert.Equal([("whisper.model", "base.en"), ("llm.provider", "none")], ModePresets.SettingsValues(ModePresets.For(ModePreset.Light, true)));
        Assert.Equal([("whisper.model", "small.en"), ("llm.provider", "local"), ("llm.model", "qwen2.5-1.5b")],
            ModePresets.SettingsValues(ModePresets.For(ModePreset.Full, false)));
        Assert.Equal("Full (large-v3-turbo + qwen2.5-1.5b)", ModePresets.Label(ModePreset.Full, true));
        Assert.Equal("Light (base.en + rule-based tidy)", ModePresets.Label(ModePreset.Light, false));
    }
}

/// <summary>The batch-2 keys in settings.json (SPEC §16).</summary>
public class Batch2SettingsKeyTests
{
    [Fact]
    public void New_keys_parse_and_default()
    {
        var defaults = SettingsLoader.Parse("{}").Settings;
        Assert.Equal((0, false, "off", (string?)null), (defaults.Whisper.BeamSize, defaults.Llm.GrammarAssist, defaults.Profanity.Mode, defaults.FixHotkey));

        // LeftCtrl: with the default RightCtrl hotkey a plain Ctrl+Shift+F overlaps it and is ignored (fix batch item 17).
        var load = SettingsLoader.Parse("""
            { "whisper": { "beam_size": 5 }, "llm": { "grammar_assist": true }, "profanity": { "mode": "Mask" }, "fix_hotkey": "LeftCtrl+Shift+F" }
            """);
        Assert.Empty(load.Warnings);
        Assert.Equal((5, true, "mask", "LeftCtrl+Shift+F"), (load.Settings.Whisper.BeamSize, load.Settings.Llm.GrammarAssist, load.Settings.Profanity.Mode, load.Settings.FixHotkey));
    }

    [Fact]
    public void Bad_values_warn_and_use_the_default()
    {
        var load = SettingsLoader.Parse("""
            { "whisper": { "beam_size": 40 }, "llm": { "grammar_assist": "yes" }, "profanity": { "mode": "bleep", "extra": 1 }, "fix_hotkey": "Fn" }
            """);
        Assert.Equal((0, false, "off", (string?)null), (load.Settings.Whisper.BeamSize, load.Settings.Llm.GrammarAssist, load.Settings.Profanity.Mode, load.Settings.FixHotkey));
        Assert.Equal(5, load.Warnings.Count);
    }

    [Fact]
    public void A_fix_hotkey_equal_to_the_hotkey_is_ignored()
    {
        var load = SettingsLoader.Parse("""{ "hotkey": "RightCtrl", "fix_hotkey": "RCtrl" }""");
        Assert.Null(load.Settings.FixHotkey);
        Assert.Contains(load.Warnings, w => w.Contains("fix_hotkey", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_file_carries_the_new_keys_and_loads_without_warnings()
    {
        var json = SettingsFile.DefaultJson("small.en");
        foreach (var key in new[] { "\"beam_size\"", "\"grammar_assist\"", "\"profanity\"", "\"fix_hotkey\"" }) Assert.Contains(key, json);
        Assert.Empty(SettingsLoader.Parse(json).Warnings);
    }
}

/// <summary>SPEC §16.5 step 4: llm.grammar_assist switches one sentence into the cleanup prompt.</summary>
public class GrammarAssistTests
{
    private static TranscriptCleaner Local(bool grammarAssist, string? dictionaryLine = null) =>
        TranscriptCleaner.Create(new LlmSettings { Provider = LlmSettings.Local, GrammarAssist = grammarAssist }, offlineOnly: false,
            SecretStore.Empty, new HttpClient(new CountingHandler()), NullLog.Instance, () => null, dictionaryLine);

    [Fact]
    public void The_prompt_gains_the_grammar_sentence_only_when_the_setting_is_on()
    {
        Assert.Contains(CleanupPrompt.GrammarAssist, Local(grammarAssist: true).SystemPrompt);
        Assert.DoesNotContain(CleanupPrompt.GrammarAssist, Local(grammarAssist: false).SystemPrompt);
        Assert.Equal(CleanupPrompt.Default, Local(grammarAssist: false).SystemPrompt);
        Assert.Equal(CleanupPrompt.Default + "\n" + CleanupPrompt.GrammarAssist + "\nKnown names and terms, keep their spelling: Kynni.",
            Local(grammarAssist: true, "Known names and terms, keep their spelling: Kynni.").SystemPrompt);
    }

    [Fact]
    public void The_validator_coverage_rules_still_apply_with_grammar_assist()
    {
        const string raw = "um so yesterday i go to the office and i meet with sarah about the budget for next quarter";
        Assert.NotNull(CleanupValidator.Validate(raw, "Yesterday I went to the office and met Sarah about the budget for the next quarter.").Text);
        Assert.Null(CleanupValidator.Validate(raw, "Yesterday I went to the office.").Text); // a dropped fact is still rejected
        Assert.Contains("system prompt", CleanupValidator.Validate(raw, CleanupPrompt.GrammarAssist).Rejection);
    }
}

/// <summary>SPEC §16.3/16.4 in the pipeline: profanity and corrections run before the paste; the last text stays in memory.</summary>
public class PipelineFinishTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finish_runs_before_the_insert_and_the_pasted_text_is_remembered(bool logTranscripts)
    {
        var inserted = new List<string>();
        var remembered = new List<string>();
        var log = new RecordingLog();
        var pipeline = new DictationPipeline(_ => Task.FromResult<ITranscriber>(new FakeTranscriber("ask kinney about the secret merger")),
            () => TranscriptCleaner.RulesOnly("off"),
            (text, _) => { inserted.Add(text); return Task.CompletedTask; },
            () => new AppSettings { LogTranscripts = logTranscripts }, log,
            finish: text => text.Replace("kinney", "Kynni", StringComparison.Ordinal),
            inserted: remembered.Add);

        Assert.Equal(ProcessingOutcome.Inserted, await pipeline.RunAsync(new CapturedAudio(new float[32000], 0.2f), CancellationToken.None, _ => { }));
        Assert.Equal(["Ask Kynni about the secret merger."], inserted);
        Assert.Equal(inserted, remembered);
        Assert.Equal(logTranscripts, log.All.Contains("secret merger", StringComparison.Ordinal));
        Assert.Contains("Corrections, the profanity filter or snippets changed the text.", log.All);
    }
}
