using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;
using Dictum.UI.Views.Settings;

namespace Dictum.Tests;

/// <summary>Languages and translation (SPEC §23, 16/10/2026): the language table, the settings, the catalogue, the guards and the decoder.</summary>
public class LanguageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_table_is_whispers_list_and_has_no_code_outside_it()
    {
        Assert.Equal(100, WhisperLanguages.Codes.Count); // openai/whisper tokenizer.py LANGUAGES, read 16/10/2026
        Assert.Equal("Spanish", WhisperLanguages.Name("es"));
        Assert.Equal("German", WhisperLanguages.Name("de"));
        Assert.Equal("English", WhisperLanguages.Name("en"));
        Assert.Equal("Detect automatically", WhisperLanguages.Name("auto"));
        Assert.True(WhisperLanguages.IsValid("auto"));
        Assert.True(WhisperLanguages.IsValid("ja"));
        Assert.False(WhisperLanguages.IsValid("eo")); // Esperanto: chosen only as an example of a code that is not in Whisper's list
        Assert.False(WhisperLanguages.IsValid("xx"));
        Assert.False(WhisperLanguages.IsValid("ES")); // the loader lower-cases before asking
        Assert.Equal("es (Spanish)", WhisperLanguages.Describe("es"));
        Assert.Equal("auto (detected per dictation)", WhisperLanguages.Describe("auto"));
    }

    [Fact]
    public void The_picker_lists_auto_then_english_then_every_other_language_alphabetically_by_name()
    {
        var items = WhisperLanguages.PickerItems();
        Assert.Equal(101, items.Count);
        Assert.Equal(("auto", "Detect automatically"), items[0]);
        Assert.Equal(("en", "English (en)"), items[1]);
        Assert.Equal(("af", "Afrikaans (af)"), items[2]); // no language is featured: the first after English is the first by name
        var rest = items.Skip(2).Select(i => WhisperLanguages.Name(i.Code)).ToList();
        Assert.Equal(rest.OrderBy(n => n, StringComparer.Ordinal), rest);
        Assert.DoesNotContain("en", items.Skip(2).Select(i => i.Code));
        Assert.Equal(items.Count, items.Select(i => i.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("medium.en", "en", false, null)]
    [InlineData("medium.en", "es", false, "English-only")]
    [InlineData(@"C:\models\ggml-small.en-q5_1.bin", "de", false, "English-only")]
    [InlineData(@"C:\my.en.models\ggml-medium.bin", "de", false, null)] // ".en" in a folder name is not the model's
    [InlineData("medium.en", "en", true, "turn Translate off")]
    [InlineData("medium", "es", false, null)]
    [InlineData("medium", "es", true, null)]
    [InlineData("small", "auto", true, null)]
    [InlineData("large-v3-turbo", "es", false, null)]
    [InlineData("large-v3-turbo", "es", true, "not trained for translation")]
    [InlineData("large-v3-turbo-q5_0", "en", true, "not trained for translation")]
    [InlineData("large-v3", "ja", true, null)]
    [InlineData("medium", "yue", false, "Cantonese")]
    [InlineData("large-v3", "yue", false, null)]
    [InlineData("large-v3-turbo", "yue", false, null)]
    [InlineData("my-own-model", "es", true, null)] // an unknown name is trusted
    [InlineData(@"D:\models\ggml-my-large-v3-yue.bin", "yue", false, "Cantonese")] // but Cantonese is allowed only for names starting with large-v3
    public void The_model_is_checked_against_the_language_and_the_translate_switch(string model, string language, bool translate, string? fragment)
    {
        var problem = WhisperLanguages.Problem(model, language, translate);
        if (fragment is null) Assert.Null(problem);
        else Assert.Contains(fragment, problem);
    }

    [Theory]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    [InlineData("es", true, true)]
    [InlineData("es", false, false)]
    [InlineData("auto", false, false)]
    [InlineData("auto", true, true)]
    public void The_output_is_english_with_translate_on_or_language_en(string language, bool translate, bool english) =>
        Assert.Equal(english, WhisperLanguages.OutputIsEnglish(language, translate));

    /// <summary>
    /// Detect automatically keeps the clean-up off even when the speech is English (the cleaner is chosen when the settings load,
    /// before whisper has heard anything), so the README tells an English speaker to choose English.
    /// </summary>
    [Fact]
    public void The_README_tells_an_English_speaker_to_choose_English_rather_than_detect_automatically()
    {
        Assert.False(WhisperLanguages.OutputIsEnglish(WhisperLanguages.Auto, translate: false)); // the rule the sentence describes
        var readme = Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "README.md")), @"\s+", " ");
        Assert.Contains("Detect automatically also switches the clean-up off for English speech, so an English speaker should choose English.",
            readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mode_note_names_only_the_presets_whose_model_cannot_do_what_is_asked()
    {
        Assert.Null(WhisperLanguages.ModeNote("en", false, gpuMachine: false));
        Assert.Null(WhisperLanguages.ModeNote("en", false, gpuMachine: true));

        var cpuSpanish = WhisperLanguages.ModeNote("es", false, gpuMachine: false)!;
        Assert.Contains("Light would write base.en", cpuSpanish);
        Assert.Contains("Full would write small.en", cpuSpanish);

        var gpuSpanish = WhisperLanguages.ModeNote("es", false, gpuMachine: true)!;
        Assert.Contains("Light would write base.en", gpuSpanish);
        Assert.DoesNotContain("Full", gpuSpanish); // large-v3-turbo hears Spanish

        var gpuSpanishTranslated = WhisperLanguages.ModeNote("es", true, gpuMachine: true)!;
        Assert.Contains("Full would write large-v3-turbo", gpuSpanishTranslated);
        Assert.Contains("not trained for translation", gpuSpanishTranslated);

        var englishTranslated = WhisperLanguages.ModeNote("en", true, gpuMachine: false)!;
        Assert.Contains("turn Translate off", englishTranslated);
    }

    [Fact]
    public void Language_and_translate_load_from_settings_and_an_unknown_code_falls_back_with_a_warning()
    {
        var load = SettingsLoader.Parse("""{ "whisper": { "language": "ES", "translate": true } }""");
        Assert.Equal("es", load.Settings.Whisper.Language);
        Assert.True(load.Settings.Whisper.Translate);
        Assert.Empty(load.Warnings);

        Assert.Equal("auto", SettingsLoader.Parse("""{ "whisper": { "language": " AUTO " } }""").Settings.Whisper.Language);

        var bad = SettingsLoader.Parse("""{ "whisper": { "language": "xx" } }""");
        Assert.Equal("en", bad.Settings.Whisper.Language);
        Assert.False(bad.Settings.Whisper.Translate);
        Assert.Contains(bad.Warnings, w => w.Contains("language", StringComparison.OrdinalIgnoreCase) && w.Contains("\"de\"", StringComparison.Ordinal));

        var defaults = new AppSettings();
        Assert.Equal("en", defaults.Whisper.Language);
        Assert.False(defaults.Whisper.Translate);
        Assert.Equal(DecodeOptions.Default, new DecodeOptions(null, 0, "en", false));
    }

    /// <summary>
    /// The settings.json written at first run (<see cref="SettingsFile.DefaultJson"/>) names English without translation: a new
    /// install dictates English, with the clean-up on, until someone chooses otherwise (decision 14, 16/10/2026: English the default).
    /// </summary>
    [Fact]
    public void The_settings_file_written_at_first_run_says_english_without_translation()
    {
        var json = SettingsFile.DefaultJson("small.en");
        var whisper = System.Text.Json.Nodes.JsonNode.Parse(json)!["whisper"]!;
        Assert.Equal("en", whisper["language"]!.GetValue<string>());
        Assert.False(whisper["translate"]!.GetValue<bool>());
        var load = SettingsLoader.Parse(json);
        Assert.Empty(load.Warnings);
        Assert.Equal(("en", false), (load.Settings.Whisper.Language, load.Settings.Whisper.Translate));
    }

    [Fact]
    public async Task A_language_or_translate_change_rebuilds_the_decoder_without_loading_the_model_again()
    {
        var loads = 0;
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => { Interlocked.Increment(ref loads); return new FakeSpeechEngine(p, d); }, gpuInUse: () => false);
        models.BeginLoad(@"C:\models\ggml-medium.bin", new WhisperSettings());
        var engine = Assert.IsType<FakeSpeechEngine>(await models.GetAsync(Ct));
        Assert.Equal("en", engine.Decode.Language);
        Assert.False(engine.Decode.Translate);

        models.SetDecoding(new DecodeOptions(null, 0, "es", true)); // what AppShell.Reload sends after settings.json changed
        Assert.True(await Eventually(() => engine.Decode.Language == "es" && engine.Decode.Translate), "the decoder was not rebuilt with Spanish and translate");
        Assert.Equal(1, loads); // the model was not loaded again
        Assert.Same(engine, await models.GetAsync(Ct));

        models.SetDecoding(new DecodeOptions(null, 0, "auto", false));
        Assert.True(await Eventually(() => engine.Decode.Language == "auto" && !engine.Decode.Translate));
        Assert.Equal(1, loads);
        models.Dispose();
    }

    /// <summary>
    /// The dictionary (SPEC §16.2) is whisper's prompt whatever the language: the tray app gives the decoder the prompt with the
    /// language (AppShell.LoadConfiguration), so names and terms are still spelled the user's way in Spanish dictation.
    /// </summary>
    [Fact]
    public async Task The_dictionary_prompt_reaches_the_decoder_for_spanish()
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "medium", "language": "es" } }""");
        var modelsDir = scratch.Paths.ModelsDir(new AppSettings());
        Directory.CreateDirectory(modelsDir);
        File.WriteAllBytes(Path.Combine(modelsDir, "ggml-medium.bin"), new byte[16]); // present; the fake loader never reads it
        File.WriteAllText(scratch.Paths.DictionaryFile, "Kynni\nParramatta\n");
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var shell = await UiSession.Run(() =>
        {
            var (s, _, _) = scratch.Shell(models);
            s.Reload(userRequested: true);
            return s;
        });
        try
        {
            var engine = Assert.IsType<FakeSpeechEngine>(await models.GetAsync(Ct));
            Assert.Equal("es", engine.Decode.Language);
            Assert.False(engine.Decode.Translate);
            Assert.Equal("Kynni, Parramatta", engine.Decode.Prompt);
        }
        finally
        {
            await UiSession.Run(() => shell.Dispose()); // disposes the model manager too
        }
    }

    /// <summary>
    /// The lens's case end to end (16/10/2026): a hand edit of whisper.language that the loaded model cannot hear keeps the model,
    /// so the tray app's reload only rebuilds the decoder; the warning notice still comes, once, however many reloads follow.
    /// </summary>
    [Fact]
    public Task A_hand_edited_language_the_loaded_model_cannot_hear_gets_one_notice() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small.en", "language": "en" } }""");
        var modelsDir = scratch.Paths.ModelsDir(new AppSettings());
        Directory.CreateDirectory(modelsDir);
        File.WriteAllBytes(Path.Combine(modelsDir, "ggml-small.en.bin"), new byte[16]); // present; the fake loader never reads it
        var log = new RecordingLog();
        var models = new ModelManager(log, loader: (p, _, d) => new FakeSpeechEngine(p, d), gpuInUse: () => false);
        var shell = new AppShell(new FakePlatform(scratch.Paths), scratch.Paths, log, new SecretRedactor(), new ShellOptions(), new ScriptedDialogs(),
            gpuMachine: () => false, speechModels: models);
        shell.Reload(userRequested: true);
        File.WriteAllText(scratch.Paths.SettingsFile, """{ "hotkey": "F24", "whisper": { "model": "small.en", "language": "es" } }"""); // the hand edit
        shell.Reload(userRequested: false);
        shell.Reload(userRequested: false); // the next save of any setting
        var notices = log.Lines.Count(l => l.Message.StartsWith("Notice: small.en is an English-only model, so language \"es\" cannot be heard", StringComparison.Ordinal));
        shell.Dispose();
        Assert.Equal(1, notices);
    });

    /// <summary>
    /// Calibration reads English sentences aloud, so it decodes English and never translates, whatever whisper.language and
    /// whisper.translate say for dictation (SPEC §23): <see cref="CalibrationHost"/> hands the loader calibration's own options.
    /// </summary>
    [Fact]
    public async Task Calibration_decodes_english_without_translation_whatever_dictation_uses()
    {
        using var scratch = new Scratch();
        var spanishTranslated = new AppSettings { Whisper = new WhisperSettings { Model = "medium", Language = "es", Translate = true } };
        var loads = new List<DecodeOptions>();
        FakeSpeechEngine? engine = null;
        var host = new CalibrationHost(scratch.Paths, () => spanishTranslated, () => PersonalText.Empty,
            new ModelIntegrity(Path.Combine(scratch.Root.FullName, "verified-models.json"), NullLog.Instance), NullLog.Instance,
            FindSpeechModel: _ => null, Write: _ => { }, DownloadSpeechModel: (_, _, _, _) => { }, AddDictionaryTerms: _ => { }, AddCorrections: _ => { },
            LoadSpeech: (path, _, decode) =>
            {
                loads.Add(decode);
                return engine = new FakeSpeechEngine(path, decode);
            });

        var results = await host.CreateEngine().RunAsync([new CalibrationClip(1, "send it to Siobhan", new float[32000])],
            [new SpeechCandidate("my-model", Path.Combine(scratch.Root.FullName, "my-model.bin"))], "Siobhan", null, Ct);

        Assert.Equal(2, results.Count); // greedy, then beam search
        var loaded = Assert.Single(loads);
        Assert.Equal(("en", false), (loaded.Language, loaded.Translate));
        Assert.Equal(DecodeOptions.CalibrationBeamSize, engine!.Decode.BeamSize); // the decoder was rebuilt for beam search…
        Assert.Equal(("en", false), (engine.Decode.Language, engine.Decode.Translate)); // …in English too
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return true;
            await Task.Delay(10, Ct);
        }
        return condition();
    }

    [Fact]
    public async Task Text_in_another_language_gets_whitespace_tidied_only_never_the_english_rules()
    {
        const string polish = "Kupiłem  chleb i mleko ";
        Assert.Equal("Kupiłem chleb I mleko.", DeterministicCleaner.Clean(polish)); // the English rules: capital "I", a full stop
        Assert.Equal("Kupiłem chleb i mleko", DeterministicCleaner.WhitespaceOnly(polish));

        var plain = TranscriptCleaner.Plain("test");
        Assert.True(plain.IsPlain);
        Assert.False(plain.UsesModel);
        Assert.Equal("Kupiłem chleb i mleko", (await plain.CleanAsync(polish, Ct)).Text);

        var rules = new LlmSettings { Provider = LlmSettings.NoProvider };
        using var http = new HttpClient();
        var skipped = TranscriptCleaner.Create(rules, false, SecretStore.Empty, http, NullLog.Instance, englishText: false);
        Assert.True(skipped.IsPlain);
        Assert.Contains("skipped", skipped.Status);
        Assert.False(TranscriptCleaner.Create(rules, false, SecretStore.Empty, http, NullLog.Instance, englishText: true).IsPlain);
    }

    // Text that is not English still goes through the corrections, the profanity filter and the snippets (SPEC §23), so they must
    // keep to whole words in its script. A vowel sign, virama or anusvara is a combining mark, not a letter: it belongs
    // to the word it is written in. Each case below changed the text, or suggested nothing, before marks counted (lens, 16/10/2026).

    [Fact]
    public void A_combining_mark_correction_replaces_the_whole_word_and_never_the_inside_of_another_word()
    {
        var rules = new Corrections(Corrections.Parse(["राम => श्याम", "कम => काम"]));
        Assert.Equal("श्याम आया", rules.Apply("राम आया"));
        Assert.Equal("मुझे काम है", rules.Apply("मुझे कम है"));
        Assert.Equal("यह विराम है", rules.Apply("यह विराम है")); // "विराम" holds "राम" after the vowel sign ि: it became "विश्याम"
        Assert.Equal("पैसे कमाओ", rules.Apply("पैसे कमाओ")); // "कमाओ" starts with "कम" before the vowel sign ा
    }

    [Fact]
    public void A_combining_mark_snippet_fires_on_its_own_word_only()
    {
        var snippets = new Corrections(Corrections.Parse(["ईमेल => kyle@example.com"], null, "snippets.txt", lineBreaks: true), verbatim: true);
        var personal = new PersonalText(PersonalDictionary.Empty, Corrections.Empty, ProfanityFilter.Empty, snippets);
        Assert.Equal("मेरा kyle@example.com भेजो", personal.Finish("मेरा ईमेल भेजो", new ProfanitySettings()));
        Assert.Equal("सारे ईमेलों को देखो", personal.Finish("सारे ईमेलों को देखो", new ProfanitySettings())); // "ईमेलों" (emails) is another word
    }

    [Theory]
    [InlineData("मुझे कम है", "मुझे काम है", "कम => काम")] // only the vowel sign ा differs
    [InlineData("वे यहाँ है", "वे यहाँ हैं", "है => हैं")] // only the anusvara differs: "is" to "are"
    public void Fix_last_suggests_a_combining_mark_correction_that_changes_only_a_mark(string pasted, string fixedText, string suggested) =>
        Assert.Equal(suggested, Assert.Single(CorrectionSuggestions.Suggest(pasted, fixedText)).ToString());

    [Fact]
    public void A_combining_mark_word_on_the_profanity_list_is_masked_after_its_first_letter()
    {
        var filter = new ProfanityFilter(["कमीना"], "test");
        Assert.Equal("वह क**** है", filter.Apply("वह कमीना है", ProfanitySettings.Mask)); // it became "क*ीना"
    }

    [Fact]
    public void The_multilingual_models_are_in_the_catalogue_with_the_sizes_and_hashes_read_from_the_pinned_tree()
    {
        // HF API tree at ModelCatalog.Revision, read 16/10/2026 (small.en, medium.en, large-v3-turbo and its q5_0 were the controls and matched).
        var expected = new Dictionary<string, (long Bytes, string Sha)>
        {
            ["small"] = (487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
            ["small-q5_1"] = (190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
            ["medium"] = (1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
            ["medium-q5_0"] = (539_212_467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f"),
            ["large-v3"] = (3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
            ["large-v3-q5_0"] = (1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        };
        foreach (var (name, (bytes, sha)) in expected)
        {
            var entry = ModelCatalog.Find(name);
            Assert.NotNull(entry);
            Assert.Equal(bytes, entry!.Bytes);
            Assert.Equal(sha, entry.Sha256);
            Assert.False(WhisperLanguages.IsEnglishOnlyModel(name));
            Assert.Equal($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{ModelCatalog.Revision}/ggml-{name}.bin", ModelCatalog.DownloadUri(name).ToString());
        }
        Assert.Equal(ModelCatalog.Entries.Count, ModelCatalog.Entries.Select(e => e.Name).Distinct().Count());
        Assert.All(ModelCatalog.Entries.Where(e => e.Name.Contains(".en", StringComparison.Ordinal)), e => Assert.True(WhisperLanguages.IsEnglishOnlyModel(e.Name)));
    }

    /// <summary>
    /// --transcribe builds its clean-up in CliCommands.Cleaner with the tray app's checks (PersonalText.ForCleanup) and the
    /// tray app's language rule. Spanish written as Spanish gets the plain cleaner: no model is asked, so no validator rule runs
    /// on the text, the dictionary-term rule included. The same settings with translation on are English again: the model
    /// and every check, the dictionary terms included.
    /// </summary>
    [Fact]
    public async Task The_transcribe_clean_up_follows_the_language_as_the_tray_app_does()
    {
        var personal = new PersonalText(new PersonalDictionary(["Dictum", "Kynni"]), Corrections.Empty, ProfanityFilter.Empty);
        var engine = new FakeLocalEngine("qwen2.5-1.5b-instruct-q4_k_m.gguf", reply: "Add Kynni to the dictionary once."); // adds a term
        using var http = new HttpClient();
        var spanish = new AppSettings { Whisper = new WhisperSettings { Model = "medium", Language = "es" } }; // llm.provider local, the default

        var plain = Dictum.Cli.CliCommands.Cleaner(spanish, SecretStore.Empty, http, NullLog.Instance, () => engine, personal);
        Assert.True(plain.IsPlain);
        Assert.False(plain.UsesModel);
        Assert.Contains("skipped", plain.Status);
        Assert.Equal(new CleanupResult("नाम एक बार", false, null), await plain.CleanAsync("नाम  एक बार ", Ct)); // whitespace only: no full stop

        var english = Dictum.Cli.CliCommands.Cleaner(spanish with { Whisper = spanish.Whisper with { Translate = true } }, SecretStore.Empty, http,
            NullLog.Instance, () => engine, personal);
        Assert.False(english.IsPlain);
        Assert.True(english.UsesModel);
        Assert.Equal(personal.Dictionary.CleanupTerms, english.Validation.DictionaryTerms!);
        Assert.Equal("reply adds a dictionary term the speaker did not say", (await english.CleanAsync("add a name to the dictionary once", Ct)).FallbackReason);
    }
}

/// <summary>
/// The Language card on Settings → Models, the Mode page's note, and the Clean-up and Privacy state lines while the text is
/// not English (SPEC §23), on Avalonia's headless platform.
/// </summary>
public class LanguageUiTests
{
    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static string Texts(ILogical root) => string.Join("\n", All<TextBlock>(root).Select(t => t.Text));

    private static ComboBox LanguagePicker(ILogical root) => All<ComboBox>(root).First(c => c.ItemCount == 101);

    private static int IndexOf(string code) => WhisperLanguages.PickerItems().ToList().FindIndex(i => i.Code == code);

    [Fact]
    public Task Choosing_spanish_is_refused_with_an_english_only_model_and_written_with_a_multilingual_one() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small.en" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var picker = LanguagePicker(window);
        Assert.Equal(IndexOf("en"), picker.SelectedIndex);

        picker.SelectedIndex = IndexOf("es");
        Assert.Contains("small.en is an English-only model", Texts(window));
        Assert.Equal(IndexOf("en"), picker.SelectedIndex); // put back by the page itself, before any reload
        Assert.Equal("en", shell.Settings.Whisper.Language);

        shell.Write(("whisper.model", System.Text.Json.Nodes.JsonValue.Create("medium")));
        picker.SelectedIndex = IndexOf("es");
        Assert.Equal("es", shell.Settings.Whisper.Language); // the in-app write reloads at once
        Assert.Contains("Dictum listens for Spanish (es). The decoder switches now; the model stays loaded.", Texts(window));
        Assert.Contains("skipped", shell.CleanupStatus); // Spanish written as Spanish: the English clean-up is off

        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// The Language card's confirmation is a sentence for every choice: a language by its own name, capital included, and Detect
    /// automatically in the card's own words, never "Dictum listens for detect automatically".
    /// </summary>
    [Fact]
    public Task The_confirmation_names_the_language_or_says_how_detect_automatically_works() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "medium" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var picker = LanguagePicker(window);

        picker.SelectedIndex = IndexOf("auto");
        Assert.Equal("auto", shell.Settings.Whisper.Language);
        Assert.Contains("Dictum works out the language from the first seconds of each dictation. The decoder switches now; the model stays loaded.", Texts(window));

        shell.Write(("whisper.translate", System.Text.Json.Nodes.JsonValue.Create(true)));
        picker.SelectedIndex = IndexOf("de");
        Assert.Contains("Dictum listens for German (de) and writes English. The decoder switches now; the model stays loaded.", Texts(window));
        picker.SelectedIndex = IndexOf("auto");
        Assert.Contains("Dictum works out the language from the first seconds of each dictation and writes English. The decoder switches now; the model stays loaded.", Texts(window));

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Translate_is_refused_on_turbo_and_written_on_medium() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "large-v3-turbo", "language": "es" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        Assert.Contains("skipped", shell.CleanupStatus);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var translate = All<ToggleSwitch>(window).Single();
        Assert.False(translate.IsChecked);

        translate.IsChecked = true;
        Assert.Contains("not trained for translation", Texts(window));
        Assert.False(translate.IsChecked); // put back by the page itself
        Assert.False(shell.Settings.Whisper.Translate);

        shell.Write(("whisper.model", System.Text.Json.Nodes.JsonValue.Create("medium")));
        translate.IsChecked = true;
        Assert.True(shell.Settings.Whisper.Translate);
        Assert.Contains("writes English whatever language you speak", Texts(window));
        Assert.DoesNotContain("skipped", shell.CleanupStatus); // English comes out, so the clean-up is back

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task The_mode_page_names_the_presets_that_cannot_hear_the_language_on_cpu_and_gpu_machines() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "medium", "language": "de" } }""");
        var (cpuShell, _, _) = scratch.Shell();
        cpuShell.Reload(userRequested: true);
        var cpuWindow = new SettingsWindow(cpuShell);
        cpuWindow.Show();
        cpuWindow.ShowPage("Mode");
        var cpuText = Texts(cpuWindow);
        Assert.Contains("Light would write base.en", cpuText);
        Assert.Contains("Full would write small.en", cpuText);
        cpuWindow.Close();
        cpuShell.Dispose();

        var gpuShell = new AppShell(new FakePlatform(scratch.Paths), scratch.Paths, NullLog.Instance, new SecretRedactor(), new ShellOptions(), new ScriptedDialogs(), gpuMachine: () => true);
        gpuShell.Reload(userRequested: true);
        var gpuWindow = new SettingsWindow(gpuShell);
        gpuWindow.Show();
        gpuWindow.ShowPage("Mode");
        var gpuText = Texts(gpuWindow);
        Assert.Contains("Light would write base.en", gpuText);
        Assert.DoesNotContain("Full would write", gpuText); // large-v3-turbo hears German
        gpuWindow.Close();
        gpuShell.Dispose();
    });

    // The product owner's lines, approved 16/10/2026 as written: pinned word for word, independently of the app's copy
    // (CloudWarningAndPresetsTests pins the first three for English text, and the fourth only by its first words).
    private static string ApprovedLocalServerLine(string host, string model) =>
        $"On: a server on this PC ({host}, model {model}) tidies each dictation. Dictum cannot see whether that server forwards your text elsewhere.";

    private const string ApprovedOfflineOnlyLine = "On, but offline only is on: nothing is sent until you switch offline only off.";
    private const string ApprovedNothingSentLine = "On, but nothing is sent: the clean-up in use runs on this PC.";
    private const string ApprovedNothingLeavesLine = "Nothing leaves this PC: speech recognition and clean-up both run here.";

    /// <summary>Every visible line of a Settings page, drawn afresh from the settings and the clean-up in use as they are now.</summary>
    private static IReadOnlyList<string> PageLines(SettingsWindow window, string page)
    {
        window.ShowPage(page == "Clean-up" ? "Privacy" : "Clean-up"); // a page is redrawn when it is moved to
        window.ShowPage(page);
        return All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
    }

    /// <summary>The Clean-up card's state line ("On: …" or "On, but …").</summary>
    private static string StateLine(SettingsWindow window) =>
        Assert.Single(PageLines(window, "Clean-up"), l => l.StartsWith("On: ", StringComparison.Ordinal) || l.StartsWith("On, ", StringComparison.Ordinal));

    /// <summary>
    /// A server on this PC (an OpenAI-compatible llm.base_url on localhost) tidies each dictation only while the clean-up in use asks
    /// it. Text that is not English gets the plain cleaner, which asks no server (SPEC §23), so Settings > Clean-up must not say a
    /// server tidies each dictation while Settings > Privacy says nothing leaves this PC (rebase lane, 7b0a809, 16/10/2026).
    /// </summary>
    [Theory]
    [InlineData("es", false, false)]   // Spanish written as Spanish
    [InlineData("auto", false, false)] // detected per dictation, written as spoken
    [InlineData("es", true, true)]     // translated: English comes out, so the server tidies it
    [InlineData("en", false, true)]
    public Task The_clean_up_page_says_a_server_on_this_PC_tidies_each_dictation_only_while_it_does(string language, bool translate, bool serverTidies) => UiSession.Run(() =>
    {
        var translateJson = translate ? "true" : "false";
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "llm": { "provider": "openai-compatible", "base_url": "http://localhost:11434/v1", "model": "llama3.2" }, "whisper": { "model": "medium", "language": "{{language}}", "translate": {{translateJson}} } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.Equal("localhost", CloudPolicy.LocalServerHost(shell.Settings.Llm)); // a server on this PC is configured in every row
        Assert.Equal(serverTidies, !shell.NothingLeaves); // and the clean-up in use asks it only when the text comes out English
        var window = new SettingsWindow(shell);
        window.Show();
        var cleanUp = StateLine(window);
        var privacy = PageLines(window, "Privacy");
        if (serverTidies)
        {
            Assert.Equal(ApprovedLocalServerLine("localhost", "llama3.2"), cleanUp);
            Assert.Contains(ApprovedLocalServerLine("localhost", "llama3.2"), privacy);
            Assert.DoesNotContain(ApprovedNothingLeavesLine, privacy);
        }
        else
        {
            Assert.Equal(ApprovedNothingSentLine, cleanUp);
            Assert.DoesNotContain(PageLines(window, "Clean-up"), l => l.Contains("server on this PC", StringComparison.Ordinal));
            Assert.Contains(ApprovedNothingLeavesLine, privacy);
            // The Offline only switch's description names the server on this PC (PROPOSED 23/10/2026, legal block D.4): it is no state line.
            Assert.DoesNotContain(privacy, l => l.Contains("server on this PC", StringComparison.Ordinal)
                && l != "No network at all: downloads, Check for updates, cloud clean-up and cloud speech are refused; a server on this PC still works.");
        }
        window.Close();
        shell.Dispose();
    });

    /// <summary>
    /// The Clean-up card's state line and Privacy's lines for cloud clean-up agreed to at a host off this PC while offline only is on:
    /// the provider is refused, so nothing is sent whatever the language.
    /// </summary>
    private static Task<(string CleanUp, IReadOnlyList<string> Privacy)> OfflineOnlyPages(string language) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "offline_only": true, "llm": { "provider": "openai-compatible", "base_url": "https://llm.example.com/v1", "model": "m" }, "whisper": { "model": "medium", "language": "{{language}}" } }""");
        CloudConsentFile.Write(scratch.Paths.ConsentFile, new CloudConsentRecord(LlmSettings.OpenAiCompatible, "llm.example.com", DateTimeOffset.UtcNow, "test"));
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: false);
        Assert.True(CloudPolicy.HasConsent(shell.Settings.Llm)); // agreed to, so the load leaves cloud clean-up as it is
        Assert.NotNull(TranscriptCleaner.OfflineRefusal(shell.Settings.Llm, shell.Settings.OfflineOnly)); // and offline only refuses it
        Assert.True(shell.NothingLeaves);
        var window = new SettingsWindow(shell);
        window.Show();
        var pages = (StateLine(window), PageLines(window, "Privacy"));
        window.Close();
        shell.Dispose();
        return pages;
    });

    /// <summary>
    /// With English text offline only is why nothing is sent, and the approved line says so. Text that is not English never reaches the
    /// provider whatever offline only says, so switching it off would send nothing: the line must not promise that it would.
    /// </summary>
    [Theory]
    [InlineData("es", ApprovedNothingSentLine)]
    [InlineData("en", ApprovedOfflineOnlyLine)]
    public async Task The_clean_up_page_names_offline_only_as_the_reason_only_for_English_text(string language, string expected)
    {
        var (cleanUp, _) = await OfflineOnlyPages(language);
        Assert.Equal(expected, cleanUp);
    }

    /// <summary>Settings > Privacy decides as Settings > Clean-up does (its own comment says so): the two pages must not disagree.</summary>
    [Theory]
    [InlineData("es", ApprovedNothingLeavesLine, ApprovedOfflineOnlyLine)]
    [InlineData("en", ApprovedOfflineOnlyLine, ApprovedNothingLeavesLine)]
    public async Task The_privacy_page_names_offline_only_as_the_reason_only_for_English_text(string language, string said, string notSaid)
    {
        var (_, privacy) = await OfflineOnlyPages(language);
        Assert.Contains(said, privacy);
        Assert.DoesNotContain(notSaid, privacy);
    }
}
