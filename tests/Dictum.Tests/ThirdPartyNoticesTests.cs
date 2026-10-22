using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Every third-party component in the published folder needs its licence notice in THIRD-PARTY-NOTICES.txt, which
/// scripts\publish.ps1 copies beside Dictum.exe for every package (docs/DECISIONS.md, "Visual C++ runtime", lens finding of
/// 17/10/2026). Entries start with a "Package: &lt;id&gt; &lt;version&gt;" line. The packages that ship are read from the
/// restore result of the project that references them all (src\Dictum.Windows\obj\project.assets.json), so a new package,
/// or a changed version whose licence has to be read again, fails here until the file says so.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    private static string Repo(params string[] parts) => Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]);

    private static string Notices => File.ReadAllText(Repo("THIRD-PARTY-NOTICES.txt"));

    /// <summary>The "Package: id version" lines of the notices file.</summary>
    private static HashSet<string> Entries(string notices) =>
        [.. Regex.Matches(notices, @"^Package: (\S+) (\S+)\r?$", RegexOptions.Multiline).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")];

    /// <summary>
    /// Every package in the restore result that puts a managed library or a Windows x64 native DLL in the output. Native
    /// runtimes arrive by build targets, not as runtime assets, so the package's own file list is read too; packages whose
    /// native files are for Linux, macOS or the web, and build-time tools, do not qualify.
    /// </summary>
    private static List<string> ShippingPackages()
    {
        var assets = Repo("src", "Dictum.Windows", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{assets} is missing: restore the solution first (dotnet build).");
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        var target = json.RootElement.GetProperty("targets").EnumerateObject().First(t => !t.Name.Contains('/')).Value; // not a runtime-specific target
        var libraries = json.RootElement.GetProperty("libraries");
        var shipping = new List<string>();
        foreach (var library in target.EnumerateObject().Where(l => l.Value.GetProperty("type").GetString() == "package"))
        {
            var managed = library.Value.TryGetProperty("runtime", out var runtime) && runtime.EnumerateObject().Any(p => !p.Name.EndsWith("/_._", StringComparison.Ordinal));
            var windowsNative = libraries.GetProperty(library.Name).GetProperty("files").EnumerateArray().Select(f => f.GetString()!)
                .Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && f.Contains("win-x64", StringComparison.Ordinal));
            if (managed || windowsNative) shipping.Add(library.Name.Replace('/', ' '));
        }
        return shipping;
    }

    /// <summary>The PackageReference elements of the four projects that make up Dictum.exe and Dictum.app (not the test or packaging projects).</summary>
    private static List<string> ReferencedPackages() =>
        [.. new[] { "Dictum.Core", "Dictum.UI", "Dictum.Windows", "Dictum.Mac" }
            .SelectMany(project => Regex.Matches(File.ReadAllText(Repo("src", project, project + ".csproj")), @"<PackageReference Include=""([^""]+)"" Version=""([^""]+)""")
                .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}"))];

    private static string Missing(IEnumerable<string> wanted, HashSet<string> entries) =>
        string.Join(", ", wanted.Where(w => !entries.Contains(w)));

    [Fact]
    public void Every_package_that_ships_files_into_the_published_folder_has_a_notice_entry_at_its_version()
    {
        var shipping = ShippingPackages();
        Assert.True(shipping.Count >= 25, $"only {shipping.Count} shipping packages were found: the restore result is not what this test expects");
        Assert.Contains("LLamaSharp.Backend.Vulkan.Windows 0.27.0", shipping); // native files arrive by build targets, from a package no csproj names
        Assert.Contains("SkiaSharp.NativeAssets.Win32 2.88.9", shipping);
        var missing = Missing(shipping, Entries(Notices));
        Assert.True(missing.Length == 0, "THIRD-PARTY-NOTICES.txt has no 'Package: <id> <version>' entry for: " + missing +
            ". Read each package's .nuspec and licence files in the NuGet cache, add or update its entry, and record the sources in docs/DECISIONS.md.");
    }

    [Fact]
    public void Every_PackageReference_of_the_three_app_projects_has_a_notice_entry_at_its_version()
    {
        var referenced = ReferencedPackages();
        Assert.True(referenced.Count >= 14, $"only {referenced.Count} PackageReference elements were found");
        var missing = Missing(referenced, Entries(Notices));
        Assert.True(missing.Length == 0, "THIRD-PARTY-NOTICES.txt has no 'Package: <id> <version>' entry for: " + missing);
    }

    /// <summary>
    /// The macOS build's packages (docs/DECISIONS.md "Dictum for macOS", 22/10/2026): every package in src/Dictum.Mac's restore
    /// result that puts a managed library or a macOS native library (a .dylib under an osx or macos folder) in Dictum.app.
    /// </summary>
    private static List<string> MacShippingPackages()
    {
        var assets = Repo("src", "Dictum.Mac", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{assets} is missing: restore the solution first (dotnet build).");
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        var target = json.RootElement.GetProperty("targets").EnumerateObject().First(t => !t.Name.Contains('/')).Value;
        var libraries = json.RootElement.GetProperty("libraries");
        var shipping = new List<string>();
        foreach (var library in target.EnumerateObject().Where(l => l.Value.GetProperty("type").GetString() == "package"))
        {
            var managed = library.Value.TryGetProperty("runtime", out var runtime) && runtime.EnumerateObject().Any(p => !p.Name.EndsWith("/_._", StringComparison.Ordinal));
            var macNative = libraries.GetProperty(library.Name).GetProperty("files").EnumerateArray().Select(f => f.GetString()!)
                .Any(f => f.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) && (f.Contains("osx", StringComparison.Ordinal) || f.Contains("macos", StringComparison.Ordinal)));
            if (managed || macNative) shipping.Add(library.Name.Replace('/', ' '));
        }
        return shipping;
    }

    [Fact]
    public void Every_package_that_ships_files_into_the_app_bundle_has_a_notice_entry_at_its_version()
    {
        var shipping = MacShippingPackages();
        Assert.True(shipping.Count >= 20, $"only {shipping.Count} shipping packages were found in the Mac restore result");
        Assert.Contains("Avalonia.Native 11.3.22", shipping);
        Assert.Contains("SkiaSharp.NativeAssets.macOS 2.88.9", shipping); // native files from a package no csproj names
        Assert.Contains("HarfBuzzSharp.NativeAssets.macOS 8.3.1.1", shipping);
        var missing = Missing(shipping, Entries(Notices));
        Assert.True(missing.Length == 0, "THIRD-PARTY-NOTICES.txt has no 'Package: <id> <version>' entry for: " + missing +
            ". Read each package's .nuspec and licence files in the NuGet cache, add or update its entry, and record the sources in docs/DECISIONS.md.");
    }

    [Fact]
    public void The_macOS_self_contained_runtime_has_an_entry()
    {
        // publish-mac.sh builds self-contained for osx-arm64, so that runtime pack ships inside Dictum.app.
        Assert.Matches(@"(?m)^Package: Microsoft\.NETCore\.App\.Runtime\.osx-arm64 \S+\r?$", Notices);
    }

    [Fact]
    public void The_self_contained_runtime_and_the_Windows_SDK_projection_have_entries()
    {
        // publish.ps1 builds self-contained, so the .NET runtime pack ships; the SDK projection comes with the net10.0-windows target.
        Assert.Contains("--self-contained", File.ReadAllText(Repo("scripts", "publish.ps1")));
        var notices = Notices;
        Assert.Matches(@"(?m)^Package: Microsoft\.NETCore\.App\.Runtime\.win-x64 \S+\r?$", notices);
        Assert.Matches(@"(?m)^Package: Microsoft\.Windows\.SDK\.NET\.Ref \S+\r?$", notices);
    }

    [Fact]
    public void Every_entry_says_which_files_it_covers_and_under_which_licence_with_which_copyright()
    {
        var blocks = Regex.Split(Notices, @"(?m)^-{78}\r?$").Skip(1).Where(b => b.TrimStart().StartsWith("Package: ", StringComparison.Ordinal)).ToList();
        Assert.True(blocks.Count >= 30, $"only {blocks.Count} package entries were found");
        foreach (var block in blocks)
        {
            var name = block.TrimStart().Split('\n')[0].Trim();
            Assert.True(Regex.IsMatch(block, @"(?m)^Ships as: \S"), $"{name}: no 'Ships as:' line");
            Assert.True(Regex.IsMatch(block, @"(?m)^Licence: \S"), $"{name}: no 'Licence:' line");
            Assert.True(Regex.IsMatch(block, @"(?m)^Copyright \(nuspec\): \S"), $"{name}: no 'Copyright (nuspec):' line");
        }
    }
}
