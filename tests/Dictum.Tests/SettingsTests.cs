using System.Text.Json.Nodes;
using Dictum.Settings;

namespace Dictum.Tests;

public class SettingsTests
{
    [Fact]
    public void Empty_object_gives_the_documented_defaults()
    {
        var result = SettingsLoader.Parse("{}");
        Assert.Empty(result.Warnings);
        var s = result.Settings;
        Assert.Equal(new AppSettings(), s);
        Assert.True(s.Enabled);
        Assert.Equal("RightCtrl", s.Hotkey);
        Assert.True(s.SuppressHotkey);
        Assert.Equal(300, s.TapMs);
        Assert.Equal(400, s.DoubleTapMs);
        Assert.Equal(300, s.MinRecordingMs);
        Assert.Equal(300, s.HandsFreeMaxSeconds);
        Assert.Equal(0.01, s.SilenceThreshold);
        Assert.Null(s.Audio.Device);
        Assert.Equal(("medium.en", "en", 0, "auto"), (s.Whisper.Model, s.Whisper.Language, s.Whisper.Threads, s.Whisper.Gpu));
        Assert.Equal(("local", "qwen2.5-1.5b", 15000, "auto"), (s.Llm.Provider, s.Llm.Model, s.Llm.TimeoutMs, s.Llm.Gpu));
        Assert.False(s.OfflineOnly);
        Assert.Equal(("paste", false, true, 150), (s.Insert.Method, s.Insert.RestoreClipboard, s.Insert.TrailingSpace, s.Insert.PasteDelayMs));
        Assert.True(s.Pill.Enabled);
        Assert.Equal("info", s.LogLevel);
        Assert.False(s.LogTranscripts);
    }

    [Fact]
    public void Partial_file_overrides_only_what_it_sets()
    {
        var result = SettingsLoader.Parse("""{ "tap_ms": 250, "whisper": { "model": "small.en" }, "insert": { "method": "type" } }""");
        Assert.Empty(result.Warnings);
        Assert.Equal(250, result.Settings.TapMs);
        Assert.Equal("small.en", result.Settings.Whisper.Model);
        Assert.Equal("en", result.Settings.Whisper.Language);
        Assert.Equal("type", result.Settings.Insert.Method);
        Assert.Equal(150, result.Settings.Insert.PasteDelayMs);
    }

    [Fact]
    public void Unknown_keys_warn_and_doc_keys_are_ignored()
    {
        var result = SettingsLoader.Parse("""{ "_doc": { "x": "y" }, "colour": "red", "whisper": { "_doc": "c", "beam": 5 } }""");
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("\"colour\""));
        Assert.Contains(result.Warnings, w => w.Contains("\"whisper.beam\""));
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Fact]
    public void Malformed_values_warn_and_fall_back_without_throwing()
    {
        var result = SettingsLoader.Parse("""
            {
              "enabled": "yes",
              "hotkey": "Fn",
              "tap_ms": "fast",
              "double_tap_ms": 99999,
              "silence_threshold": 5,
              "audio": 3,
              "whisper": { "model": "../../evil", "gpu": "cuda", "threads": -1 },
              "llm": { "provider": "openai", "base_url": "not a url", "timeout_ms": 1.5 },
              "insert": { "method": "teleport", "trailing_space": null },
              "log_level": 7
            }
            """);
        var d = new AppSettings();
        var s = result.Settings;
        Assert.Equal(d.Enabled, s.Enabled);
        Assert.Equal(d.Hotkey, s.Hotkey);
        Assert.Equal(d.TapMs, s.TapMs);
        Assert.Equal(d.DoubleTapMs, s.DoubleTapMs);
        Assert.Equal(d.SilenceThreshold, s.SilenceThreshold);
        Assert.Equal(d.Whisper, s.Whisper);
        Assert.Equal(d.Llm, s.Llm);
        Assert.Equal(d.Insert, s.Insert);
        Assert.Equal(d.LogLevel, s.LogLevel);
        Assert.Equal(15, result.Warnings.Count); // one per bad value, none swallowed
        Assert.Contains(result.Warnings, w => w.Contains("\"hotkey\"") && w.Contains("firmware"));
    }

    [Fact]
    public void Invalid_json_uses_defaults_with_one_warning()
    {
        var result = SettingsLoader.Parse("{ \"tap_ms\": 250, ");
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        var result = SettingsLoader.Parse("""
            {
              // hold time
              "tap_ms": 280, /* block */
            }
            """);
        Assert.Empty(result.Warnings);
        Assert.Equal(280, result.Settings.TapMs);
    }

    [Fact]
    public void Default_file_round_trips_without_warnings()
    {
        var json = SettingsFile.DefaultJson("small.en");
        var result = SettingsLoader.Parse(json);
        Assert.Empty(result.Warnings);
        Assert.Equal("small.en", result.Settings.Whisper.Model);
        Assert.Contains("\"_doc\"", json);
    }

    [Fact]
    public void Set_value_updates_one_key_and_keeps_the_rest()
    {
        var dir = Directory.CreateTempSubdirectory("dictate-settings-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """{ "tap_ms": 222, "whisper": { "model": "small.en" } }""");
            Assert.True(SettingsFile.SetValue(path, "enabled", JsonValue.Create(false)));
            Assert.True(SettingsFile.SetValue(path, "whisper.model", JsonValue.Create("medium.en")));
            var s = SettingsLoader.LoadFile(path).Settings;
            Assert.False(s.Enabled);
            Assert.Equal(222, s.TapMs);
            Assert.Equal("medium.en", s.Whisper.Model);

            File.WriteAllText(path, "{ broken");
            Assert.False(SettingsFile.SetValue(path, "enabled", JsonValue.Create(true)));
            Assert.Equal("{ broken", File.ReadAllText(path)); // never overwrites a file it cannot parse
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_and_reports_it()
    {
        var result = SettingsLoader.LoadFile(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"));
        Assert.False(result.FileExisted);
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Theory]
    [InlineData("small.en", "ggml-small.en.bin")]
    [InlineData("medium.en-q5_0", "ggml-medium.en-q5_0.bin")]
    [InlineData("large-v3-turbo", "ggml-large-v3-turbo.bin")]
    public void Model_short_name_resolves_into_models_dir(string name, string file)
    {
        var dir = @"C:\models";
        Assert.Equal(Path.Combine(dir, file), ModelCatalog.ResolvePath(name, dir));
        Assert.True(ModelCatalog.IsValidModelSetting(name));
    }

    [Fact]
    public void Absolute_model_path_is_used_as_is_and_traversal_is_rejected()
    {
        Assert.Equal(TestPaths.Native(@"D:\x\my-model.bin"), ModelCatalog.ResolvePath(TestPaths.Native(@"D:\x\my-model.bin"), TestPaths.Native(@"C:\models")));
        Assert.False(ModelCatalog.IsValidModelSetting("../evil"));
        Assert.False(ModelCatalog.IsValidModelSetting(@"relative\path.bin"));
        Assert.Equal("my-model", ModelCatalog.DisplayName(TestPaths.Native(@"D:\x\ggml-my-model.bin")));
    }

    [Fact]
    public void Models_dir_setting_and_environment_override()
    {
        var paths = new AppPaths(TestPaths.Native(@"C:\cfg"), TestPaths.Native(@"C:\local"), TestPaths.Native(@"C:\exe"));
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "small.en" } };
        Assert.Equal(TestPaths.Native(@"C:\local\models\ggml-small.en.bin"), SettingsFile.EffectiveModelPath(settings, paths, _ => null));

        var custom = settings with { Whisper = settings.Whisper with { ModelsDir = TestPaths.Native(@"E:\m") } };
        Assert.Equal(TestPaths.Native(@"E:\m\ggml-small.en.bin"), SettingsFile.EffectiveModelPath(custom, paths, _ => null));

        Assert.Equal(TestPaths.Native(@"F:\override.bin"), SettingsFile.EffectiveModelPath(settings, paths, k => k == "DICTUM_MODEL_PATH" ? $"\"{TestPaths.Native(@"F:\override.bin")}\"" : null));
    }

    [Fact]
    public void Model_catalogue_matches_the_spec_and_the_verified_sizes()
    {
        var expected = new Dictionary<string, long>
        {
            ["tiny.en"] = 77_704_715,          // HTTP HEAD 15/10/2026
            ["base.en"] = 147_964_211,         // HTTP HEAD 15/10/2026
            ["small.en"] = 487_614_201,
            ["small.en-q5_1"] = 190_098_681,
            ["small.en-q8_0"] = 264_477_561,
            ["medium.en"] = 1_533_774_781,
            ["medium.en-q5_0"] = 539_225_533,
            ["medium.en-q8_0"] = 823_382_461,
            ["large-v3-turbo"] = 1_624_555_275,
            ["large-v3-turbo-q5_0"] = 574_041_195,
            // Multilingual (SPEC §23): HF API tree at ModelCatalog.Revision, read 16/10/2026.
            ["small"] = 487_601_967,
            ["small-q5_1"] = 190_085_487,
            ["medium"] = 1_533_763_059,
            ["medium-q5_0"] = 539_212_467,
            ["large-v3"] = 3_095_033_483,
            ["large-v3-q5_0"] = 1_081_140_203,
        };
        Assert.Equal(expected.Keys, ModelCatalog.Entries.Select(e => e.Name));
        foreach (var e in ModelCatalog.Entries)
        {
            Assert.Equal(expected[e.Name], e.Bytes);
            Assert.Equal($"ggml-{e.Name}.bin", e.FileName);
            Assert.False(string.IsNullOrWhiteSpace(e.TradeOff));
        }
        Assert.Null(ModelCatalog.KnownSize("tiny"));
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.en.bin", ModelCatalog.DownloadUri("small.en").ToString());
        Assert.Equal("1.5 GB", ModelCatalog.Find("medium.en")!.SizeText);
        Assert.Equal("487 MB", ModelCatalog.Find("small.en")!.SizeText);
        Assert.Equal("medium.en · 1.5 GB · downloaded", ModelCatalog.Find("medium.en")!.ItemText(true));
        Assert.Equal("small.en · 487 MB · not downloaded", ModelCatalog.Find("small.en")!.ItemText(false));
    }

    [Fact]
    public void Recommendation_rule_gpu_medium_cpu_base_with_small_offered()
    {
        Assert.Equal("medium.en", ModelCatalog.Recommend(gpuRuntime: true).Model);
        var cpu = ModelCatalog.Recommend(gpuRuntime: false);
        Assert.Equal("base.en", cpu.Model);
        Assert.Contains("small.en is more accurate, a few seconds slower", cpu.Reason);
        Assert.Contains("a few seconds slower", ModelCatalog.Find("small.en")!.TradeOff);
    }

    [Fact]
    public void Launch_override_beats_environment_and_settings()
    {
        var paths = new AppPaths(TestPaths.Native(@"C:\cfg"), TestPaths.Native(@"C:\local"), TestPaths.Native(@"C:\exe"));
        var settings = new AppSettings { Whisper = new WhisperSettings { Model = "small.en" } };
        string? Env(string k) => k == "DICTUM_MODEL_PATH" ? TestPaths.Native(@"F:\env.bin") : null;
        Assert.Equal(TestPaths.Native(@"C:\local\models\ggml-tiny.en.bin"), SettingsFile.EffectiveModelPath(settings, paths, Env, "tiny.en"));
        Assert.Equal(TestPaths.Native(@"G:\x.bin"), SettingsFile.EffectiveModelPath(settings, paths, Env, TestPaths.Native(@"G:\x.bin")));
        Assert.Equal(TestPaths.Native(@"F:\env.bin"), SettingsFile.EffectiveModelPath(settings, paths, Env, null));
    }

    [Fact]
    public void Scripts_use_the_app_models_folder()
    {
        TestEnv.WindowsOnly("Checks the Windows PowerShell scripts against the Windows models-folder layout (%LOCALAPPDATA%\\Dictum\\models).");
        // Regression: install.ps1 -ModelsFrom once copied into "%LOCALAPPDATA%\Dictummodels" (a lost backslash),
        // found by the scratch-profile install on 15/10/2026.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dictum.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var expected = Path.GetRelativePath(@"C:\L", new AppPaths(@"C:\C", @"C:\L\Dictum", @"C:\E").DefaultModelsDir); // Dictum\models
        var download = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "download-model.ps1"));
        Assert.Contains($"Join-Path $env:LOCALAPPDATA \"{expected}\"", download); // its -ModelsDir default
        // install.ps1 asks the installed app (whisper.models_dir included); FixBatchDownloadTests runs it in a scratch profile.
        var install = File.ReadAllText(Path.Combine(dir.FullName, "scripts", "install.ps1"));
        Assert.Contains("$modelsDir = (& $exe --print-models-dir | Out-String).Trim()", install);
        foreach (var text in new[] { download, install }) Assert.DoesNotContain("Dictummodels", text);
    }

    [Fact]
    public void Download_script_catalogues_are_in_sync_with_the_app()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Dictum.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "scripts", "download-model.ps1"));

        var speech = System.Text.RegularExpressions.Regex.Matches(script, @"""(?<name>[a-z0-9.\-_]+)""\s*=\s*@\((?<bytes>\d+),\s*""(?<sha>[0-9a-f]{64})""\)")
            .ToDictionary(m => m.Groups["name"].Value, m => (long.Parse(m.Groups["bytes"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["sha"].Value));
        Assert.Equal(ModelCatalog.Entries.ToDictionary(e => e.Name, e => (e.Bytes, e.Sha256)), speech);

        var cleanup = System.Text.RegularExpressions.Regex.Matches(script, @"""(?<name>[a-z0-9.\-_]+)""\s*=\s*@\(""(?<repo>[^""]+)"",\s*""(?<file>[^""]+)"",\s*(?<bytes>\d+),\s*(?:""(?<sha>[0-9a-f]{64})""|\$null),\s*\$(?<gated>true|false),\s*""(?<rev>[0-9a-f]{40})""\)")
            .ToDictionary(m => m.Groups["name"].Value, m => (m.Groups["repo"].Value, m.Groups["file"].Value, long.Parse(m.Groups["bytes"].Value, System.Globalization.CultureInfo.InvariantCulture),
                m.Groups["sha"].Success ? m.Groups["sha"].Value : null, m.Groups["gated"].Value == "true", m.Groups["rev"].Value));
        Assert.Equal(CleanupCatalog.Entries.ToDictionary(e => e.Name, e => (e.Repo, e.FileName, e.Bytes, e.Sha256, e.RequiresLogin, e.Revision)), cleanup);
        // The URL and file-name templates themselves (a comment elsewhere in the script must not satisfy this).
        Assert.Contains($"$speechRevision = \"{ModelCatalog.Revision}\"", script);
        Assert.Contains("$repo = \"ggerganov/whisper.cpp\"; $revision = $speechRevision;", script);
        Assert.Contains("    $file = \"ggml-$Model.bin\"\n", script.ReplaceLineEndings("\n"));
        Assert.Contains("\n$url = \"https://huggingface.co/$repo/resolve/$revision/$file\"\n", script.ReplaceLineEndings("\n"));
    }
}

public class EnvFileTests
{
    [Fact]
    public void Parses_quotes_comments_crlf_export_and_bom()
    {
        var content = "\uFEFF# secrets\r\nANTHROPIC_API_KEY=sk-ant-plain\r\n\r\nexport LLM_API_KEY = \"quoted value # not a comment\"\r\nSINGLE='it''s raw \\n'\r\nESCAPED=\"line1\\nline2\"\r\nINLINE=value # trailing comment\r\nEMPTY=\r\n=novalue\r\nnot a pair\r\n";
        var env = EnvFile.Parse(content);
        Assert.Equal("sk-ant-plain", env["ANTHROPIC_API_KEY"]);
        Assert.Equal("quoted value # not a comment", env["LLM_API_KEY"]);
        Assert.Equal("it", env["SINGLE"]);
        Assert.Equal("line1\nline2", env["ESCAPED"]);
        Assert.Equal("value", env["INLINE"]);
        Assert.Equal("", env["EMPTY"]);
        Assert.Equal(6, env.Count);
    }

    [Fact]
    public void Lf_only_files_parse_the_same()
    {
        var env = EnvFile.Parse("A=1\nB=\"2\"\n");
        Assert.Equal("1", env["A"]);
        Assert.Equal("2", env["B"]);
    }

    [Fact]
    public void Environment_wins_then_appdata_env_then_exe_folder_env()
    {
        var store = SecretStore.FromContents(
            appDataEnv: "ANTHROPIC_API_KEY=from-appdata\nLLM_API_KEY=appdata-llm\n",
            exeDirEnv: "ANTHROPIC_API_KEY=from-exe\nLLM_API_KEY=exe-llm\nEXE_ONLY=exe-only\n",
            environment: k => k switch { "ANTHROPIC_API_KEY" => "from-env", "OTHER" => "env-other", _ => null });
        Assert.Equal("from-env", store.Get("ANTHROPIC_API_KEY"));   // a machine-level variable wins (SPEC §5)
        Assert.Equal("appdata-llm", store.Get("LLM_API_KEY"));      // AppData .env over exe-folder .env (SPEC §9)
        Assert.Equal("exe-only", store.Get("EXE_ONLY"));            // .env fills gaps
        Assert.Equal("env-other", store.Get("OTHER"));
        Assert.Null(store.Get("MISSING"));
    }

    [Fact]
    public void Empty_environment_variable_does_not_hide_the_env_file()
    {
        var store = SecretStore.FromContents("ANTHROPIC_API_KEY=from-file\n", null, _ => "");
        Assert.Equal("from-file", store.Get("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void Describe_never_reveals_the_value()
    {
        var store = SecretStore.FromContents("ANTHROPIC_API_KEY=sk-ant-secret-123\n", null, _ => null);
        var text = store.Describe("ANTHROPIC_API_KEY");
        Assert.DoesNotContain("sk-ant", text);
        Assert.StartsWith("present", text);
        Assert.Equal("absent", store.Describe("LLM_API_KEY"));
    }
}
