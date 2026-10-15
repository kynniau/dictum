using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>Cleanup settings left by the first build are repaired at load (the file is not rewritten).</summary>
public class SettingsRepairTests
{
    private static readonly AppSettings FirstBuild = new() { Llm = new LlmSettings { Provider = "anthropic", Model = "claude-haiku-5-5" } };

    [Fact]
    public void Anthropic_without_a_key_becomes_local_with_the_recommended_model()
    {
        var warnings = new List<string>();
        var s = EffectiveSettings.Cleanup(FirstBuild, anthropicKeyPresent: false, gpuRuntime: () => true, warnings.Add);
        Assert.Equal(("local", "qwen2.5-1.5b"), (s.Llm.Provider, s.Llm.Model));
        Assert.Equal(2, warnings.Count);
        Assert.Contains("ANTHROPIC_API_KEY is not set", warnings[0]);
        Assert.Contains("\"claude-haiku-5-5\" is not a cleanup model name", warnings[1]);
        Assert.Equal("qwen2.5-1.5b", EffectiveSettings.Cleanup(FirstBuild, false, () => false, _ => { }).Llm.Model); // CPU recommendation, measured
    }

    [Fact]
    public void Anthropic_with_a_key_is_left_alone()
    {
        var warnings = new List<string>();
        var consented = FirstBuild with { Llm = FirstBuild.Llm with { CloudConsent = "api.anthropic.com" } }; // SPEC §22: agreed in the app
        Assert.Same(consented, EffectiveSettings.Cleanup(consented, anthropicKeyPresent: true, () => throw new InvalidOperationException("not asked"), warnings.Add));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("claude-haiku-5-5", "qwen2.5-1.5b")]
    [InlineData("llama3.2", "qwen2.5-1.5b")]
    [InlineData("qwen2.5-0.5b", "qwen2.5-0.5b")]
    [InlineData(@"D:\models\mine.gguf", @"D:\models\mine.gguf")]
    public void A_local_model_that_is_not_a_cleanup_model_uses_the_recommendation(string model, string expected)
    {
        var settings = new AppSettings { Llm = new LlmSettings { Provider = "local", Model = model } };
        Assert.Equal(expected, EffectiveSettings.Cleanup(settings, false, () => true, _ => { }).Llm.Model);
    }

    [Fact]
    public void Other_providers_keep_their_model_ids()
    {
        var settings = new AppSettings { Llm = new LlmSettings { Provider = "openai-compatible", Model = "llama3.2", BaseUrl = "http://localhost:11434/v1" } };
        Assert.Same(settings, EffectiveSettings.Cleanup(settings, false, () => true, _ => { }));
    }
}

/// <summary>First-use SHA-256 check of catalogue files Dictum did not download itself.</summary>
public sealed class ModelIntegrityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-integrity-");

    /// <summary>Writes a file with this name (a catalogue name or not) into the temporary folder.</summary>
    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void A_file_outside_the_catalogue_is_not_checked()
    {
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance);
        Assert.Equal("not in the catalogue, so not hash-checked", integrity.Verify(Write("my-own.gguf", [1, 2, 3])));
    }

    [Fact]
    public void A_catalogue_file_with_the_wrong_size_is_refused_without_hashing()
    {
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance);
        var ex = Assert.Throws<ModelIntegrityException>(() => integrity.Verify(Write("ggml-tiny.en.bin", new byte[1000])));
        Assert.Contains("is 1,000 bytes but the catalogue says 77,704,715; it was not loaded", ex.Message);
    }

    [Fact]
    public void A_catalogue_file_with_the_right_size_but_wrong_content_is_refused_and_not_remembered()
    {
        var cache = Path.Combine(_dir.FullName, "v.json");
        var log = new RecordingLog();
        var integrity = new ModelIntegrity(cache, log);
        var path = Path.Combine(_dir.FullName, "ggml-tiny.en.bin");
        using (var f = File.Create(path)) f.SetLength(77_704_715); // zeros: right size, wrong SHA-256
        var ex = Assert.Throws<ModelIntegrityException>(() => integrity.Verify(path));
        Assert.Contains("does not match the catalogue (SHA-256", ex.Message);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f"));
        Assert.False(File.Exists(cache));
        Assert.Throws<ModelIntegrityException>(() => integrity.Verify(path)); // still refused next time
    }

    [Fact]
    public void A_gated_entry_without_a_published_hash_is_size_checked_only()
    {
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance);
        var path = Path.Combine(_dir.FullName, "gemma-3-1b-it-q4_0.gguf");
        using (var f = File.Create(path)) f.SetLength(1_003_541_152);
        Assert.Equal("size matches; no published SHA-256 to check", integrity.Verify(path));
    }

    [Fact]
    public void A_matching_real_model_is_hashed_once_then_remembered()
    {
        var real = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(real) || ModelIntegrity.Expected(real) is null, "Set DICTUM_MODEL_PATH to a catalogue model file.");
        var cache = Path.Combine(_dir.FullName, "v.json");
        var log = new RecordingLog();
        var integrity = new ModelIntegrity(cache, log);
        Assert.Equal("SHA-256 verified", integrity.Verify(real!));
        Assert.Equal("SHA-256 verified earlier", integrity.Verify(real!));
        Assert.Single(log.Lines, l => l.Message.StartsWith("Verified ", StringComparison.Ordinal)); // logged with the time it took
    }

    /// <summary>Fix batch #26: the record's last-write time is what makes a same-size replacement get hashed again.</summary>
    [Fact]
    public void A_remembered_model_whose_timestamp_changed_is_hashed_again()
    {
        var real = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(real) || ModelIntegrity.Expected(real) is null, "Set DICTUM_MODEL_PATH to a catalogue model file.");
        var copy = Path.Combine(_dir.FullName, Path.GetFileName(real)!); // a copy: the real file's timestamp is never touched
        File.Copy(real!, copy);
        var integrity = new ModelIntegrity(Path.Combine(_dir.FullName, "v.json"), NullLog.Instance);
        Assert.Equal("SHA-256 verified", integrity.Verify(copy));
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(copy).AddMinutes(-5)); // same size, replaced in place
        Assert.Equal("SHA-256 verified", integrity.Verify(copy)); // hashed again, not "verified earlier"
    }

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
