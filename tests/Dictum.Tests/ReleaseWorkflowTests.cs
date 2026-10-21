using System.Text.RegularExpressions;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// CI and release plumbing added with the Visual C++ runtime fix (docs/DECISIONS.md, "Visual C++ runtime", lens findings
/// of 17/10/2026): the 148 MB base.en speech model is cached between CI runs under its pinned SHA-256, and a release's
/// notes open with a fixed preamble that names the runtime requirement and the bundled model. These read the workflow and
/// the pages as text; the workflow itself runs only on GitHub, so its first real run is the proof of the YAML.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static string Repo(params string[] parts) => Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]);

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Ci => File.ReadAllText(Repo(".github", "workflows", "ci.yml"));

    private static string Preamble => File.ReadAllText(Repo("packaging", "release-notes-preamble.md"));

    /// <summary>The oldest runtime the scripts accept ($MinVcRuntime in scripts\build-common.ps1), as "14.51".</summary>
    private static string Floor => $"{VcRuntimeLayout.MinVcRuntime.Major}.{VcRuntimeLayout.MinVcRuntime.Minor}";

    [Fact]
    public void Every_action_in_the_workflow_is_pinned_to_a_commit()
    {
        var uses = Regex.Matches(Ci, @"^\s*(?:- )?uses: (\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Contains(uses, u => u.StartsWith("actions/cache@", StringComparison.Ordinal));
        Assert.All(uses, u => Assert.Matches(@"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", u));
    }

    [Fact]
    public void The_model_cache_is_keyed_on_the_catalogues_base_en_SHA_256_and_restored_before_publish()
    {
        var model = ModelCatalog.Find("base.en")!;
        var step = Regex.Match(Ci, @"- name: Restore the base\.en speech model from the cache.*?(?=\n      - )", RegexOptions.Singleline);
        Assert.True(step.Success, "ci.yml has no step named 'Restore the base.en speech model from the cache'");
        Assert.Contains("uses: actions/cache@", step.Value);
        // Whole lines: "path: dist/model-cachee" or a key with a suffix must not pass.
        Assert.Matches(@"(?m)^\s*path: dist/model-cache\s*$", step.Value);
        Assert.Matches($@"(?m)^\s*key: {Regex.Escape($"model-ggml-{model.Name}-{model.Sha256}")}\s*$", step.Value);
        Assert.True(step.Index < Ci.IndexOf("run: ./scripts/publish.ps1", StringComparison.Ordinal), "the cache is restored after the publish step has already fetched the model");
    }

    [Fact]
    public void The_release_job_checks_out_the_preamble_and_puts_it_ahead_of_the_generated_notes()
    {
        var release = Ci[Ci.IndexOf("\n  release:", StringComparison.Ordinal)..];
        var checkout = release.IndexOf("uses: actions/checkout@", StringComparison.Ordinal);
        Assert.True(checkout >= 0, "the release job has no checkout, so the preamble file is not there");
        Assert.True(checkout < release.IndexOf("uses: actions/download-artifact@", StringComparison.Ordinal), "the checkout comes after the artifacts are downloaded");
        var create = Regex.Match(release, @"^\s*gh release create .*$", RegexOptions.Multiline).Value;
        Assert.Contains("--notes-file packaging/release-notes-preamble.md", create);
        Assert.Contains("--generate-notes", create);
    }

    [Fact]
    public void The_preamble_names_the_runtime_floor_the_installer_checks_and_the_bundled_model()
    {
        var preamble = Squash(Preamble);
        Assert.Contains($"Microsoft Visual C++ Redistributable (x64), version {Floor} or later", preamble);
        Assert.Contains("https://aka.ms/vc14/vc_redist.x64.exe", preamble);
        Assert.Contains(@"models\ggml-base.en.bin", preamble);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", preamble);
    }

    [Fact]
    public void The_README_and_the_preamble_state_the_same_runtime_floor()
    {
        Assert.Contains($"Microsoft Visual C++ Redistributable (x64), version {Floor} or later", Squash(File.ReadAllText(Repo("README.md"))));
        Assert.Contains($"version {Floor} or later", Squash(Preamble));
    }

    [Fact]
    public void The_site_names_no_runtime_because_the_Store_installs_it()
    {
        // Site: Store-first (docs/DECISIONS.md, 22/10/2026). The Store installs the VCLibs framework the package declares,
        // so the site's "What you need" list no longer carries the direct download's runtime requirement.
        foreach (var page in new[] { "index.html", "help.html" })
        {
            var html = File.ReadAllText(Repo("site", page));
            Assert.DoesNotContain("Visual C++", html, StringComparison.Ordinal);
            Assert.DoesNotContain("vc_redist", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
