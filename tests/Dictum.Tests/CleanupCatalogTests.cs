using Dictum.Cli;
using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Tests;

public class CleanupCatalogTests
{
    [Fact]
    public void Catalogue_matches_the_spec_sizes_hashes_and_licences()
    {
        // Read 15/10/2026: HEAD X-Linked-Size (tree API size for the gated Gemma repos), HF API lfs.oid, model cards.
        var expected = new Dictionary<string, (string Vendor, long Bytes, string? Sha, bool Commercial, bool Login)>
        {
            ["qwen2.5-0.5b"] = ("Qwen", 491_400_032, "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db", true, false),
            ["qwen2.5-1.5b"] = ("Qwen", 1_117_320_736, "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", true, false),
            ["smollm2-1.7b"] = ("HuggingFaceTB", 1_055_609_536, "decd2598bc2c8ed08c19adc3c8fdd461ee19ed5708679d1c54ef54a5a30d4f33", true, false),
            ["gemma-3-1b"] = ("Google", 1_003_541_152, null, true, true),
            ["gemma-3-4b"] = ("Google", 3_155_051_328, null, true, true),
        };
        Assert.Equal(expected.Keys, CleanupCatalog.Entries.Select(e => e.Name));
        foreach (var e in CleanupCatalog.Entries)
        {
            Assert.Equal(expected[e.Name], (e.Vendor, e.Bytes, e.Sha256, e.CommercialUse, e.RequiresLogin));
            Assert.Equal($"https://huggingface.co/{e.Repo}/resolve/{e.Revision}/{e.FileName}", e.DownloadUri.ToString()); // pinned to a commit (fix batch #25)
            Assert.Matches("^[0-9a-f]{40}$", e.Revision);
            Assert.Equal(e.RequiresLogin, e.Download.RequiresLogin);
            Assert.StartsWith("https://", e.LicenceUrl);
        }
        Assert.Equal("gemma-3-1b · 1.0 GB · not downloaded · requires a Hugging Face login · size-checked only · Gemma Terms of Use apply (ai.google.dev/gemma/terms)", CleanupCatalog.Find("gemma-3-1b")!.ItemText(false));
        Assert.Equal("qwen2.5-1.5b · 1.1 GB · not downloaded", CleanupCatalog.Find("qwen2.5-1.5b")!.ItemText(false)); // an open-source licence adds nothing
    }

    /// <summary>
    /// Pin (legal block, owner 23/10/2026 09:50 AEDT, section D item 9): no catalogue entry is under a non-commercial licence
    /// (qwen2.5-3b, under the Qwen Research License, was removed, here and in download-model.ps1), so Dictum may be used at work
    /// with any model it lists (Terms section 2); and the two Gemma entries name Google's Gemma Terms of Use wherever they are
    /// listed (APPROVED by the owner 23/10/2026 13:20 AEDT, legal strings block 2: "Gemma Terms of Use apply (ai.google.dev/gemma/terms)"), the others nothing.
    /// </summary>
    [Fact]
    public void No_catalogue_entry_is_non_commercial_and_the_Gemma_entries_name_their_terms()
    {
        Assert.All(CleanupCatalog.Entries, e => Assert.True(e.CommercialUse, $"{e.Name} is under a non-commercial licence ({e.Licence})"));
        Assert.All(CleanupCatalog.Entries, e => Assert.DoesNotContain("non-commercial", e.Licence, StringComparison.OrdinalIgnoreCase));
        Assert.All(CleanupCatalog.Entries, e => Assert.DoesNotContain("Research", e.Licence, StringComparison.OrdinalIgnoreCase));
        Assert.Null(CleanupCatalog.Find("qwen2.5-3b"));
        Assert.Equal(["gemma-3-1b", "gemma-3-4b"], CleanupCatalog.Entries.Where(e => e.LicenceNote is not null).Select(e => e.Name));
        Assert.All(CleanupCatalog.Entries.Where(e => e.LicenceNote is not null), e =>
        {
            Assert.Equal("Gemma Terms of Use apply (ai.google.dev/gemma/terms)", e.LicenceNote);
            Assert.EndsWith(" · Gemma Terms of Use apply (ai.google.dev/gemma/terms)", e.Marker);
            Assert.Equal("https://ai.google.dev/gemma/terms", e.LicenceUrl);
        });
        var script = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "scripts", "download-model.ps1"));
        Assert.DoesNotContain("qwen2.5-3b", script);
        Assert.DoesNotContain("Research License", script);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_a_commercial_ungated_hash_pinned_model_is_recommended(bool gpu)
    {
        var info = CleanupCatalog.Find(CleanupCatalog.Recommend(gpu).Model)!;
        Assert.True(info.CommercialUse);
        Assert.False(info.RequiresLogin);
        Assert.NotNull(info.Sha256);
        Assert.StartsWith("qwen2.5-", info.Name);
    }

    [Fact]
    public async Task A_gated_model_is_refused_with_the_login_message_before_any_request()
    {
        var spec = CleanupCatalog.Find("gemma-3-1b")!.Download;
        var http = new HttpClient(new CountingHandler());
        var ex = await Assert.ThrowsAsync<Dictum.Transcribe.DownloadFailedException>(() => Dictum.Transcribe.ModelDownloader.DownloadAsync(
            http, spec, Path.Combine(Path.GetTempPath(), "dictate-never", spec.FileName), false, new Progress<(long, long?)>(), TestContext.Current.CancellationToken));
        Assert.Contains("requires a Hugging Face login to download; download it in your browser, then choose it in Settings > Models", ex.Message);
        Assert.Equal(0, CountingHandler.Requests);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public static int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public void Model_setting_resolves_to_the_catalogue_file_or_an_absolute_gguf()
    {
        Assert.Equal(TestPaths.Native(@"C:\m\qwen2.5-1.5b-instruct-q4_k_m.gguf"), CleanupCatalog.ResolvePath("qwen2.5-1.5b", TestPaths.Native(@"C:\m")));
        Assert.Equal(TestPaths.Native(@"D:\x\my.gguf"), CleanupCatalog.ResolvePath(TestPaths.Native(@"D:\x\my.gguf"), TestPaths.Native(@"C:\m")));
        Assert.True(CleanupCatalog.IsValidSetting(TestPaths.Native(@"D:\x\my.gguf")));
        Assert.False(CleanupCatalog.IsValidSetting("qwen9"));
        Assert.False(CleanupCatalog.IsValidSetting(TestPaths.Native(@"D:\x\my.bin")));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(20, 64)]
    [InlineData(100, 182)]
    [InlineData(5000, 2048)]
    public void Local_max_tokens_is_clamped(int inputTokens, int expected) => Assert.Equal(expected, CleanupExamples.LocalMaxTokens(inputTokens));

    [Fact]
    public void Three_short_few_shot_pairs_are_supplied() => Assert.Equal(3, CleanupExamples.Pairs.Count);

    [Theory]
    [InlineData("Successfully loaded 'C:\\app\\runtimes\\win-x64\\native\\avx2\\ggml-cpu.dll'", null)]
    [InlineData("Failed loading 'C:\\app\\llama.dll'", null)]
    public void Loaded_backend_is_read_from_the_loader_log(string line, string? expected) =>
        Assert.Equal(expected, LocalLlmEngine.LoadedLibraryPath(line));

    [Fact]
    public void The_loaded_llama_library_path_is_read_back_whole()
    {
        // A folder with a space, and the mixed separators llama.cpp's loader logs: the path comes back as it was logged.
        var path = Path.Combine(Path.GetTempPath(), "a folder with spaces", "bin") + @"\runtimes/win-x64/native/vulkan/llama.dll";
        Assert.Equal(path, LocalLlmEngine.LoadedLibraryPath($"Successfully loaded '{path}'"));
    }

    [Fact]
    public void Cli_parses_cleanup_flags()
    {
        var a = CliOptions.Parse(["--cleanup-model", "qwen2.5-0.5b", "--transcribe", "x.wav"]).Options!;
        Assert.Equal((CliMode.Transcribe, "qwen2.5-0.5b"), (a.Mode, a.CleanupModel));
        var b = CliOptions.Parse(["--download-cleanup-model", "qwen2.5-1.5b"]).Options!;
        Assert.Equal((CliMode.DownloadCleanupModel, "qwen2.5-1.5b"), (b.Mode, b.DownloadName));
        Assert.Null(CliOptions.Parse(["--download-cleanup-model", "llama-70b"]).Options);
        Assert.Null(CliOptions.Parse(["--cleanup-model", "../x.gguf"]).Options);
    }

    [Fact]
    public void Cleanup_override_switches_this_launch_to_the_local_provider()
    {
        var settings = new AppSettings { Llm = new LlmSettings { Provider = "none" } };
        var over = SettingsFile.WithCleanupOverride(settings, "qwen2.5-0.5b");
        Assert.Equal(("local", "qwen2.5-0.5b"), (over.Llm.Provider, over.Llm.Model));
        Assert.Same(settings, SettingsFile.WithCleanupOverride(settings, null));
    }

    [Fact]
    public void Settings_accept_the_local_provider_llm_gpu_and_offline_only()
    {
        var r = SettingsLoader.Parse("""{ "offline_only": true, "llm": { "provider": "local", "model": "qwen2.5-0.5b", "gpu": "cpu" } }""");
        Assert.Empty(r.Warnings);
        Assert.True(r.Settings.OfflineOnly);
        Assert.Equal(("local", "qwen2.5-0.5b", "cpu"), (r.Settings.Llm.Provider, r.Settings.Llm.Model, r.Settings.Llm.Gpu));
        Assert.Contains("\"offline_only\"", SettingsFile.DefaultJson("small.en"));
    }
}
