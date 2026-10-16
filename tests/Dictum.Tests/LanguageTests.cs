using Avalonia.Controls;
using Avalonia.LogicalTree;
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
    public void The_table_is_whispers_list_with_hindi_and_tagalog_but_no_cebuano()
    {
        Assert.Equal(100, WhisperLanguages.Codes.Count); // openai/whisper tokenizer.py LANGUAGES, read 16/10/2026
        Assert.Equal("Hindi", WhisperLanguages.Name("hi"));
        Assert.Equal("Tagalog", WhisperLanguages.Name("tl"));
        Assert.Equal("English", WhisperLanguages.Name("en"));
        Assert.Equal("Detect automatically", WhisperLanguages.Name("auto"));
        Assert.True(WhisperLanguages.IsValid("auto"));
        Assert.True(WhisperLanguages.IsValid("bn"));
        Assert.False(WhisperLanguages.IsValid("ceb")); // Cebuano is not in Whisper
        Assert.False(WhisperLanguages.IsValid("fil"));
        Assert.False(WhisperLanguages.IsValid("HI")); // the loader lower-cases before asking
        Assert.All(WhisperLanguages.Featured, code => Assert.True(WhisperLanguages.IsValid(code), code));
        Assert.Equal("hi (Hindi)", WhisperLanguages.Describe("hi"));
        Assert.Equal("auto (detected per dictation)", WhisperLanguages.Describe("auto"));
    }

    [Fact]
    public void The_picker_lists_auto_then_english_hindi_tagalog_then_the_indian_languages_then_the_rest_by_name()
    {
        var items = WhisperLanguages.PickerItems();
        Assert.Equal(101, items.Count);
        Assert.Equal(("auto", "Detect automatically"), items[0]);
        Assert.Equal(("en", "English (en)"), items[1]);
        Assert.Equal(("hi", "Hindi (hi)"), items[2]);
        Assert.Equal(("tl", "Tagalog (tl)"), items[3]);
        Assert.Equal(WhisperLanguages.Featured, items.Skip(1).Take(WhisperLanguages.Featured.Length).Select(i => i.Code));
        var rest = items.Skip(1 + WhisperLanguages.Featured.Length).Select(i => i.Label).ToList();
        Assert.Equal(rest.OrderBy(l => l, StringComparer.Ordinal), rest);
        Assert.Equal(items.Count, items.Select(i => i.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("medium.en", "en", false, null)]
    [InlineData("medium.en", "hi", false, "English-only")]
    [InlineData(@"C:\models\ggml-small.en-q5_1.bin", "tl", false, "English-only")]
    [InlineData(@"C:\my.en.models\ggml-medium.bin", "tl", false, null)] // ".en" in a folder name is not the model's
    [InlineData("medium.en", "en", true, "turn Translate off")]
    [InlineData("medium", "hi", false, null)]
    [InlineData("medium", "hi", true, null)]
    [InlineData("small", "auto", true, null)]
    [InlineData("large-v3-turbo", "hi", false, null)]
    [InlineData("large-v3-turbo", "hi", true, "not trained for translation")]
    [InlineData("large-v3-turbo-q5_0", "en", true, "not trained for translation")]
    [InlineData("large-v3", "bn", true, null)]
    [InlineData("medium", "yue", false, "Cantonese")]
    [InlineData("large-v3", "yue", false, null)]
    [InlineData("large-v3-turbo", "yue", false, null)]
    [InlineData("my-own-model", "hi", true, null)] // an unknown name is trusted
    public void The_model_is_checked_against_the_language_and_the_translate_switch(string model, string language, bool translate, string? fragment)
    {
        var problem = WhisperLanguages.Problem(model, language, translate);
        if (fragment is null) Assert.Null(problem);
        else Assert.Contains(fragment, problem);
    }

    [Theory]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    [InlineData("hi", true, true)]
    [InlineData("hi", false, false)]
    [InlineData("auto", false, false)]
    [InlineData("auto", true, true)]
    public void The_output_is_english_with_translate_on_or_language_en(string language, bool translate, bool english) =>
        Assert.Equal(english, WhisperLanguages.OutputIsEnglish(language, translate));

    [Fact]
    public void The_mode_note_names_only_the_presets_whose_model_cannot_do_what_is_asked()
    {
        Assert.Null(WhisperLanguages.ModeNote("en", false, gpuMachine: false));
        Assert.Null(WhisperLanguages.ModeNote("en", false, gpuMachine: true));

        var cpuHindi = WhisperLanguages.ModeNote("hi", false, gpuMachine: false)!;
        Assert.Contains("Light would write base.en", cpuHindi);
        Assert.Contains("Full would write small.en", cpuHindi);

        var gpuHindi = WhisperLanguages.ModeNote("hi", false, gpuMachine: true)!;
        Assert.Contains("Light would write base.en", gpuHindi);
        Assert.DoesNotContain("Full", gpuHindi); // large-v3-turbo hears Hindi

        var gpuHindiTranslated = WhisperLanguages.ModeNote("hi", true, gpuMachine: true)!;
        Assert.Contains("Full would write large-v3-turbo", gpuHindiTranslated);
        Assert.Contains("not trained for translation", gpuHindiTranslated);

        var englishTranslated = WhisperLanguages.ModeNote("en", true, gpuMachine: false)!;
        Assert.Contains("turn Translate off", englishTranslated);
    }

    [Fact]
    public void Language_and_translate_load_from_settings_and_an_unknown_code_falls_back_with_a_warning()
    {
        var load = SettingsLoader.Parse("""{ "whisper": { "language": "HI", "translate": true } }""");
        Assert.Equal("hi", load.Settings.Whisper.Language);
        Assert.True(load.Settings.Whisper.Translate);
        Assert.Empty(load.Warnings);

        Assert.Equal("auto", SettingsLoader.Parse("""{ "whisper": { "language": " AUTO " } }""").Settings.Whisper.Language);

        var bad = SettingsLoader.Parse("""{ "whisper": { "language": "ceb" } }""");
        Assert.Equal("en", bad.Settings.Whisper.Language);
        Assert.False(bad.Settings.Whisper.Translate);
        Assert.Contains(bad.Warnings, w => w.Contains("language", StringComparison.OrdinalIgnoreCase) && w.Contains("\"tl\"", StringComparison.Ordinal));

        var defaults = new AppSettings();
        Assert.Equal("en", defaults.Whisper.Language);
        Assert.False(defaults.Whisper.Translate);
        Assert.Equal(DecodeOptions.Default, new DecodeOptions(null, 0, "en", false));
    }

    [Fact]
    public async Task A_language_or_translate_change_rebuilds_the_decoder_without_loading_the_model_again()
    {
        var loads = 0;
        var models = new ModelManager(NullLog.Instance, loader: (p, _, d) => { Interlocked.Increment(ref loads); return new FakeSpeechEngine(p, d); }, warmUp: () => false);
        models.BeginLoad(@"C:\models\ggml-medium.bin", new WhisperSettings());
        var engine = Assert.IsType<FakeSpeechEngine>(await models.GetAsync(Ct));
        Assert.Equal("en", engine.Decode.Language);
        Assert.False(engine.Decode.Translate);

        models.SetDecoding(new DecodeOptions(null, 0, "hi", true)); // what AppShell.Reload sends after settings.json changed
        Assert.True(await Eventually(() => engine.Decode.Language == "hi" && engine.Decode.Translate), "the decoder was not rebuilt with Hindi and translate");
        Assert.Equal(1, loads); // the model was not loaded again
        Assert.Same(engine, await models.GetAsync(Ct));

        models.SetDecoding(new DecodeOptions(null, 0, "auto", false));
        Assert.True(await Eventually(() => engine.Decode.Language == "auto" && !engine.Decode.Translate));
        Assert.Equal(1, loads);
        models.Dispose();
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
    /// tray app's language rule. Hindi written as Hindi gets the plain cleaner: no model is asked, so no validator rule runs
    /// on the text, the dictionary-term rule included. The same settings with translation on are English again: the model
    /// and every check, the dictionary terms included.
    /// </summary>
    [Fact]
    public async Task The_transcribe_clean_up_follows_the_language_as_the_tray_app_does()
    {
        var personal = new PersonalText(new PersonalDictionary(["Dictum", "Kynni"]), Corrections.Empty, ProfanityFilter.Empty);
        var engine = new FakeLocalEngine("qwen2.5-1.5b-instruct-q4_k_m.gguf", reply: "Add Kynni to the dictionary once."); // adds a term
        using var http = new HttpClient();
        var hindi = new AppSettings { Whisper = new WhisperSettings { Model = "medium", Language = "hi" } }; // llm.provider local, the default

        var plain = Dictum.Cli.CliCommands.Cleaner(hindi, SecretStore.Empty, http, NullLog.Instance, () => engine, personal);
        Assert.True(plain.IsPlain);
        Assert.False(plain.UsesModel);
        Assert.Contains("skipped", plain.Status);
        Assert.Equal(new CleanupResult("नाम एक बार", false, null), await plain.CleanAsync("नाम  एक बार ", Ct)); // whitespace only: no full stop

        var english = Dictum.Cli.CliCommands.Cleaner(hindi with { Whisper = hindi.Whisper with { Translate = true } }, SecretStore.Empty, http,
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
    public Task Choosing_hindi_is_refused_with_an_english_only_model_and_written_with_a_multilingual_one() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "small.en" } }""");
        var (shell, _, _) = scratch.Shell();
        shell.Reload(userRequested: true);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("Models");
        var picker = LanguagePicker(window);
        Assert.Equal(IndexOf("en"), picker.SelectedIndex);

        picker.SelectedIndex = IndexOf("hi");
        Assert.Contains("small.en is an English-only model", Texts(window));
        Assert.Equal(IndexOf("en"), picker.SelectedIndex); // put back by the page itself, before any reload
        Assert.Equal("en", shell.Settings.Whisper.Language);

        shell.Write(("whisper.model", System.Text.Json.Nodes.JsonValue.Create("medium")));
        picker.SelectedIndex = IndexOf("hi");
        Assert.Equal("hi", shell.Settings.Whisper.Language); // the in-app write reloads at once
        Assert.Contains("listens for hindi (hi)", Texts(window));
        Assert.Contains("the model stays loaded", Texts(window));
        Assert.Contains("skipped", shell.CleanupStatus); // Hindi written as Hindi: the English clean-up is off

        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Translate_is_refused_on_turbo_and_written_on_medium() => UiSession.Run(() =>
    {
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "large-v3-turbo", "language": "hi" } }""");
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
        using var scratch = new Scratch("""{ "hotkey": "F24", "whisper": { "model": "medium", "language": "tl" } }""");
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
        Assert.DoesNotContain("Full would write", gpuText); // large-v3-turbo hears Tagalog
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
    [InlineData("hi", false, false)]   // Hindi written as Hindi
    [InlineData("auto", false, false)] // detected per dictation, written as spoken
    [InlineData("hi", true, true)]     // translated: English comes out, so the server tidies it
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
            Assert.DoesNotContain(privacy, l => l.Contains("server on this PC", StringComparison.Ordinal));
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
    [InlineData("hi", ApprovedNothingSentLine)]
    [InlineData("en", ApprovedOfflineOnlyLine)]
    public async Task The_clean_up_page_names_offline_only_as_the_reason_only_for_English_text(string language, string expected)
    {
        var (cleanUp, _) = await OfflineOnlyPages(language);
        Assert.Equal(expected, cleanUp);
    }

    /// <summary>Settings > Privacy decides as Settings > Clean-up does (its own comment says so): the two pages must not disagree.</summary>
    [Theory]
    [InlineData("hi", ApprovedNothingLeavesLine, ApprovedOfflineOnlyLine)]
    [InlineData("en", ApprovedOfflineOnlyLine, ApprovedNothingLeavesLine)]
    public async Task The_privacy_page_names_offline_only_as_the_reason_only_for_English_text(string language, string said, string notSaid)
    {
        var (_, privacy) = await OfflineOnlyPages(language);
        Assert.Contains(said, privacy);
        Assert.DoesNotContain(notSaid, privacy);
    }
}
