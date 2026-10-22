using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dictum.Tests;

/// <summary>
/// The macOS bundle's packaging inputs (packaging/mac, scripts/publish-mac.sh and the build-mac job of
/// .github/workflows/ci.yml). Nothing here runs a Mac: it reads the files, so a change that drops a required key, an
/// entitlement, a native library or the artefact upload fails on any platform.
/// </summary>
public sealed class MacPackagingTests
{
    private static string Repo(params string[] parts) => Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]);

    /// <summary>A property list's top-level dictionary as key to value element (&lt;string&gt;, &lt;true/&gt;, ...).</summary>
    private static Dictionary<string, XElement> Plist(string path)
    {
        var dict = XDocument.Load(path).Root!.Element("dict")!;
        var keys = dict.Elements("key").ToList();
        var values = dict.Elements().Where(e => e.Name != "key").ToList();
        Assert.Equal(keys.Count, values.Count);
        return keys.Zip(values).ToDictionary(p => p.First.Value, p => p.Second);
    }

    [Fact]
    public void Info_plist_template_carries_the_bundle_identity_and_the_approved_microphone_sentence()
    {
        var plist = Plist(Repo("packaging", "mac", "Info.plist"));
        Assert.Equal("au.kynni.dictum", plist["CFBundleIdentifier"].Value);
        Assert.Equal("Dictum", plist["CFBundleExecutable"].Value);
        Assert.Equal("APPL", plist["CFBundlePackageType"].Value);
        Assert.Equal("Dictum", plist["CFBundleIconFile"].Value);
        Assert.Equal("true", plist["LSUIElement"].Name.LocalName); // menu-bar app, no Dock icon
        Assert.Equal("true", plist["NSHighResolutionCapable"].Name.LocalName);
        Assert.Equal("15.0", plist["LSMinimumSystemVersion"].Value); // .NET 10 supports macOS 15 and later
        Assert.Equal("public.app-category.productivity", plist["LSApplicationCategoryType"].Value);
        // PROPOSED (docs/MAC-HANDOVER.md §0, public-copy lane 22/10/2026): true with cloud speech (1.3) on as well.
        Assert.Equal("Dictum listens only while you hold your hotkey, and keeps the audio on this Mac unless you turn on cloud speech.", plist["NSMicrophoneUsageDescription"].Value);
        Assert.Equal("© 2026 Kynni Pty Ltd", plist["NSHumanReadableCopyright"].Value);
        // The script fills both version keys from Directory.Build.props.
        Assert.Equal("@VERSION@", plist["CFBundleShortVersionString"].Value);
        Assert.Equal("@VERSION@", plist["CFBundleVersion"].Value);
        Assert.Contains("<Version>", File.ReadAllText(Repo("Directory.Build.props")));
    }

    [Fact]
    public void Entitlements_hold_exactly_the_five_hardened_runtime_keys()
    {
        var plist = Plist(Repo("packaging", "mac", "Dictum.entitlements"));
        Assert.Equal(
            new[]
            {
                "com.apple.security.cs.allow-dyld-environment-variables", "com.apple.security.cs.allow-jit",
                "com.apple.security.cs.allow-unsigned-executable-memory", "com.apple.security.cs.disable-library-validation",
                "com.apple.security.device.audio-input",
            },
            plist.Keys.Order(StringComparer.Ordinal));
        Assert.All(plist.Values, v => Assert.Equal("true", v.Name.LocalName));
    }

    [Fact]
    public void Workflow_has_a_mac_job_that_publishes_with_the_script_and_uploads_the_zip_and_the_release_waits_for_it()
    {
        var yaml = File.ReadAllText(Repo(".github", "workflows", "ci.yml"));
        Assert.Matches(new Regex(@"^  build-mac:\r?$", RegexOptions.Multiline), yaml);
        Assert.Contains("runs-on: macos-15", yaml);
        Assert.Contains("./scripts/publish-mac.sh", yaml);
        Assert.Contains("name: dictum-osx-arm64-zip", yaml);
        Assert.Contains("dist/Dictum-*-osx-arm64.zip", yaml);
        Assert.Matches(new Regex(@"needs: \[build, build-mac\]"), yaml);
        // Secrets are not available in `if:`; the job env carries a boolean and the steps test that.
        Assert.DoesNotMatch(new Regex(@"^\s*if:.*secrets\.", RegexOptions.Multiline), yaml);
        foreach (var secret in new[] { "MAC_SIGN_CERT_P12_BASE64", "MAC_SIGN_CERT_PASSWORD", "MAC_NOTARY_KEY_ID", "MAC_NOTARY_ISSUER_ID", "MAC_NOTARY_KEY_P8_BASE64" })
            Assert.Contains($"secrets.{secret}", yaml);
    }

    [Fact]
    public void Script_signs_without_deep_names_its_environment_variables_and_never_holds_a_credential()
    {
        var script = File.ReadAllText(Repo("scripts", "publish-mac.sh"));
        Assert.Contains("DICTUM_MAC_SIGN_IDENTITY", script);
        Assert.Contains("DICTUM_MAC_NOTARY_PROFILE", script);
        Assert.Contains("--options runtime", script);
        // --deep only on verify: man codesign (macOS 27) calls it deprecated for signing as of macOS 13.0.
        Assert.DoesNotContain(script.Split('\n'), l => l.Contains("codesign") && l.Contains("--deep") && !l.Contains("--verify") && !l.TrimStart().StartsWith('#'));
        Assert.DoesNotContain("--password", script);
        Assert.DoesNotContain(".p12", script);
    }

    /// <summary>The library names the script's two "for n in ...; do required+=(runtimes/...)" loops require.</summary>
    private static HashSet<string> ScriptRequired(string folder)
    {
        var script = File.ReadAllText(Repo("scripts", "publish-mac.sh"));
        var m = Regex.Match(script, @"for n in ([^;\n]+); do\s*\r?\n\s*required\+=\(""runtimes/" + Regex.Escape(folder) + @"/\$n\.dylib""\)");
        Assert.True(m.Success, $"publish-mac.sh has no required-natives loop for runtimes/{folder}");
        return [.. m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static string PackageDir(string id, string folderUnderPackage)
    {
        var csproj = File.ReadAllText(Repo("src", "Dictum.Mac", "Dictum.Mac.csproj"));
        var version = Regex.Match(csproj, @"<PackageReference Include=""" + Regex.Escape(id) + @""" Version=""([^""]+)""").Groups[1].Value;
        Assert.NotEmpty(version);
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var dir = Path.Combine(root, id.ToLowerInvariant(), version, folderUnderPackage);
        Assert.True(Directory.Exists(dir), $"{dir} is missing: restore the solution first (dotnet build).");
        return dir;
    }

    private static HashSet<string> Dylibs(string dir) =>
        [.. Directory.GetFiles(dir, "*.dylib").Select(f => Path.GetFileNameWithoutExtension(f))];

    [Fact]
    public void Script_requires_exactly_the_arm64_whisper_natives_the_runtime_package_ships()
    {
        // Whisper.net.Runtime copies build/macos-arm64/*.dylib to runtimes/macos-arm64/ (its .targets; publish output seen 22/10/2026).
        var shipped = Dylibs(PackageDir("Whisper.net.Runtime", Path.Combine("build", "macos-arm64")));
        Assert.Contains("runtimes/macos-arm64/", File.ReadAllText(Path.Combine(PackageDir("Whisper.net.Runtime", "build"), "Whisper.net.Runtime.targets")));
        Assert.Equal(shipped.Order(), ScriptRequired("macos-arm64").Select(n => n).Order());
    }

    [Fact]
    public void Script_requires_exactly_the_arm64_llama_natives_the_backend_package_ships()
    {
        var shipped = Dylibs(PackageDir("LLamaSharp.Backend.Cpu", Path.Combine("LLamaSharpRuntimes", "osx-arm64", "native")));
        Assert.Equal(shipped.Order(), ScriptRequired("osx-arm64/native").Order());
    }
}
