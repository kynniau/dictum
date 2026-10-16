using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// Decision 16 (17/10/2026): every package layout carries the base.en speech model beside Dictum.exe
/// (models\ggml-base.en.bin) and THIRD-PARTY-NOTICES.txt for its licence, so first use and Store testing need no
/// download. scripts\publish.ps1 places both; these tests pin what it bundles to the app's catalogue and, given
/// DICTUM_PUBLISHED_DIR (as for <see cref="VcRuntimeTests"/>), check a real layout byte for byte.
/// </summary>
public sealed class BundledModelTests
{
    private static string Repo(params string[] parts) => Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]);

    private static ModelInfo Bundled()
    {
        var match = Regex.Match(File.ReadAllText(Repo("scripts", "publish.ps1")), @"\$bundledModel = ""([^""]+)""");
        Assert.True(match.Success, @"scripts\publish.ps1 names no $bundledModel");
        var model = ModelCatalog.Find(match.Groups[1].Value);
        Assert.NotNull(model);
        return model;
    }

    [Fact]
    public void The_bundled_model_is_base_en_and_its_notice_names_exactly_the_catalogue_bytes_and_both_licensors()
    {
        var model = Bundled();
        Assert.Equal("base.en", model.Name);
        var notice = File.ReadAllText(Repo("THIRD-PARTY-NOTICES.txt"));
        Assert.Contains($@"models\{model.FileName}", notice);
        Assert.Contains($"{ModelCatalog.Revision}/{model.FileName}", notice);
        Assert.Contains($"{model.Bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes", notice);
        Assert.Contains(model.Sha256, notice);
        Assert.Contains("Copyright (c) 2022 OpenAI", notice);
        Assert.Contains("Copyright (c) 2023-2026 The ggml authors", notice);
    }

    /// <summary>Gated on DICTUM_PUBLISHED_DIR: dist\Dictum from publish.ps1, or an MSIX layout.</summary>
    [Fact]
    public void A_published_layout_carries_the_catalogue_model_and_its_notice()
    {
        var layout = TestEnv.Get("PUBLISHED_DIR");
        Assert.SkipWhen(layout is null, @"DICTUM_PUBLISHED_DIR is not set (dist\Dictum from publish.ps1, or packaging\out\msix-store\layout).");
        var model = Bundled();
        var path = Path.Combine(layout, "models", model.FileName);
        Assert.True(File.Exists(path), $"{path} is missing");
        Assert.Equal(model.Bytes, new FileInfo(path).Length);
        using (var stream = File.OpenRead(path)) Assert.Equal(model.Sha256, Convert.ToHexStringLower(SHA256.HashData(stream)));
        Assert.True(File.Exists(Path.Combine(layout, "THIRD-PARTY-NOTICES.txt")), "THIRD-PARTY-NOTICES.txt is missing");
    }
}
