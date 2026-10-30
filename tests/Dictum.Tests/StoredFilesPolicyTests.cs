using System.Text.RegularExpressions;
using Dictum.Settings;
using Dictum.Storage;

namespace Dictum.Tests;

/// <summary>
/// The privacy policy names every file Dictum stores (legal block, APPROVED by the owner 23/10/2026 09:50 AEDT; build item 11's sibling). The
/// names are read from the app's own path registry (<see cref="PathRegistry.Locations"/>, the list Settings > Storage and the two deletions use),
/// so a new stored file fails <see cref="The_registry_stores_exactly_the_files_the_policy_lists"/> until it is added to <see cref="PolicyNames"/>,
/// and then <see cref="The_privacy_policy_names_every_file_Dictum_stores"/> until site/privacy.html names it. Mutations (observed failing,
/// 23/10/2026): snippets.txt taken off the page; a fake registry name added to <see cref="PolicyNames"/>.
/// </summary>
public sealed class StoredFilesPolicyTests
{
    /// <summary>
    /// Each registry entry (by its file name, or its folder and pattern) and the name the policy gives it. A file being saved (".tmp") is one of the
    /// policy's "Working files"; the files inside the calibration and logs folders go by the folder's name.
    /// </summary>
    private static readonly Dictionary<string, string> PolicyNames = new(StringComparer.Ordinal)
    {
        ["settings.json"] = "settings.json",
        ["settings.json.tmp"] = "Working files",
        ["consent.json"] = "consent.json",
        ["consent.json.tmp"] = "Working files",
        ["dictionary.txt"] = "dictionary.txt",
        ["corrections.txt"] = "corrections.txt",
        ["snippets.txt"] = "snippets.txt",
        ["profanity.txt"] = "profanity.txt",
        [".env"] = ".env",
        // API keys, Core (30/10/2026): keys.json holds the stored keys' labels. site/privacy.html must name it before this merges; the line is PROPOSED in
        // .demo-preview/approve-api-keys.md row 15 and awaits the owner, so The_privacy_policy_names_every_file_Dictum_stores fails until then.
        ["keys.json"] = "keys.json",
        ["keys.json.tmp"] = "Working files",
        ["verified-models.json"] = "verified-models.json",
        ["verified-models.json.tmp"] = "Working files",
        ["gpu-load-speech.txt"] = "gpu-load-speech.txt",
        ["gpu-load-cleanup.txt"] = "gpu-load-cleanup.txt",
        ["calibration/clip-*"] = "calibration",
        ["calibration/takes.txt"] = "calibration",
        ["logs/*.log"] = "logs",
        ["models/" + ModelFiles.Pattern] = "models",
    };

    /// <summary>The registry's stored items under a scratch profile that is never created: its own folders (removed once empty) aside.</summary>
    private static SortedSet<string> FromRegistry()
    {
        var root = Path.Combine(Path.GetTempPath(), "dictum-policy-names");
        var paths = new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "local"), Path.Combine(root, "exe"));
        return new SortedSet<string>(
            PathRegistry.Locations(paths, new AppSettings())
                .Where(l => l.Category != DataCategory.AppFolder)
                .Select(l => l.Pattern is { } pattern ? $"{Path.GetFileName(l.Path)}/{pattern}" : Path.GetFileName(l.Path)),
            StringComparer.Ordinal);
    }

    [Fact]
    public void The_registry_stores_exactly_the_files_the_policy_lists()
    {
        var registry = FromRegistry();
        Assert.True(registry.Count >= 15, "the registry was read"); // two .env entries (settings folder and beside Dictum.exe) are one name
        Assert.Equal(PolicyNames.Keys.Order(StringComparer.Ordinal), registry);
    }

    [Fact]
    public void The_privacy_policy_names_every_file_Dictum_stores()
    {
        var policy = Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html")), "<!--.*?-->", "", RegexOptions.Singleline);
        foreach (var name in FromRegistry().Select(n => PolicyNames.GetValueOrDefault(n, n)).Distinct())
        {
            // A file or folder name appears as code, as the policy writes every one; "Working files" is the list item that describes them.
            var written = name == "Working files" ? "<li>Working files: one while a download or a save is under way, removed when it ends;" : $"<code>{name}</code>";
            Assert.True(policy.Contains(written, StringComparison.Ordinal), $"site/privacy.html does not name {name}, which Dictum stores");
        }
    }

    /// <summary>
    /// The other way round: every file name the policy's storage section writes as code (a .json, .txt or .env name with a stem) is one the
    /// registry stores, so the policy cannot list a file Dictum does not keep. Mutation (observed failing, 23/10/2026): a fake
    /// <c>history.json</c> item added to the section.
    /// </summary>
    [Fact]
    public void Every_file_the_privacy_policy_lists_is_one_Dictum_stores()
    {
        var policy = Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html")), "<!--.*?-->", "", RegexOptions.Singleline);
        const string open = "<section aria-labelledby=\"where\">";
        var start = policy.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "site/privacy.html has no storage section");
        var section = policy[start..policy.IndexOf("</section>", start, StringComparison.Ordinal)];
        var listed = Regex.Matches(section, @"<code>([\w.-]+\.(json|txt|env))</code>").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("settings.json", listed); // the scan really reads the list
        var stored = FromRegistry().Select(n => n.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(listed.Where(name => !stored.Contains(name)).Order(StringComparer.Ordinal));
    }
}
