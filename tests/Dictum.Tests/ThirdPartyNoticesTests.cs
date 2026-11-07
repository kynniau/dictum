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

    /// <summary>
    /// Pin (legal block, owner 23/10/2026 09:50 AEDT, section D item 8): the copyright lines of the upstream LICENSE files of
    /// llama.cpp, Avalonia, NAudio and LLamaSharp sit beside the .nuspec line of every entry that ships their code (read
    /// 23/10/2026: Avalonia at tag 11.3.22, LLamaSharp at v0.27.0, NAudio at v3.1.0, llama.cpp on master), each directly under its
    /// entry's "Copyright (nuspec):" line.
    /// </summary>
    [Theory]
    [InlineData("LLamaSharp", "Copyright (c) 2025 SciSharp STACK")]
    [InlineData("LLamaSharp.Backend.Cpu", "Copyright (c) 2023-2026 The ggml authors")]
    [InlineData("LLamaSharp.Backend.Vulkan", "Copyright (c) 2023-2026 The ggml authors")]
    [InlineData("LLamaSharp.Backend.Vulkan.Windows", "Copyright (c) 2023-2026 The ggml authors")]
    [InlineData("NAudio.Core", "Copyright 2008-2026 Mark Heath")]
    [InlineData("NAudio.Wasapi", "Copyright 2008-2026 Mark Heath")]
    [InlineData("Avalonia", "Copyright (c) AvaloniaUI OÜ")]
    [InlineData("Avalonia.Themes.Fluent", "Copyright (c) AvaloniaUI OÜ")]
    [InlineData("Avalonia.Win32", "Copyright (c) AvaloniaUI OÜ")]
    [InlineData("Avalonia.Native", "Copyright (c) AvaloniaUI OÜ")]
    [InlineData("Avalonia.Skia", "Copyright (c) AvaloniaUI OÜ")]
    [InlineData("Avalonia.Remote.Protocol", "Copyright (c) AvaloniaUI OÜ")]
    public void The_upstream_copyright_line_sits_beside_the_nuspec_line(string package, string upstream)
    {
        var block = Regex.Split(Notices, @"(?m)^-{78}\r?$").Single(b => Regex.IsMatch(b, $@"^\s*Package: {Regex.Escape(package)} \S+\r?$", RegexOptions.Multiline));
        Assert.Matches($@"(?m)^Copyright \(nuspec\): [^\r\n]+\r?\nCopyright \(upstream licence\): {Regex.Escape(upstream)}\r?$", block);
    }

    /// <summary>
    /// Pin (orchestrator's sherpa-onnx licence audit, 07/11/2026): the code statically linked into sherpa-onnx-c-api 2.0.1, at the
    /// versions sherpa-onnx's build pins at commit af2d8d81, each with its licence and its source at that version. A heading is the
    /// line between two 78-dash rules; the entry's text is the block after it.
    /// </summary>
    [Theory]
    [InlineData("kaldi-native-fbank 1.22.4 ", "Licence: Apache-2.0", "https://github.com/csukuangfj/kaldi-native-fbank/blob/v1.22.4/LICENSE")]
    [InlineData("KISS FFT, commit febd4caeed32e33ad8b2e0bb5ea77542c40f18ec ", "Licence: BSD-3-Clause", "Copyright (c) 2003-2010 Mark Borgerding")]
    [InlineData("kaldi-decoder 0.3.0 ", "Licence: Apache-2.0", "https://github.com/k2-fsa/kaldi-decoder/blob/v0.3.0/LICENSE")]
    [InlineData("kaldifst 1.8.0 ", "Licence: Apache-2.0", "https://github.com/k2-fsa/kaldifst/blob/v1.8.0/LICENSE")]
    [InlineData("OpenFst 1.8.5, csukuangfj fork tag v1.8.5-2026-07-09 ", "Licence: Apache-2.0", "Copyright 2005-2026 Google LLC.")]
    [InlineData("Eigen 5.0.1 ", "Licence: MPL-2.0", "https://gitlab.com/libeigen/eigen/-/archive/5.0.1/eigen-5.0.1.tar.gz")]
    [InlineData("simple-sentencepiece 0.7 ", "Licence: Apache-2.0", "https://github.com/pkufool/simple-sentencepiece/blob/v0.7/LICENSE")]
    [InlineData("darts-clone, as copied into simple-sentencepiece 0.7 ", "Licence: BSD-2-Clause", "Copyright (c) 2008-2014, Susumu Yata")]
    [InlineData("utf8proc 2.12.0 ", "Unicode data licence", "Copyright (c) 1991-2007 Unicode, Inc.")]
    [InlineData("nlohmann/json 3.12.0 ", "Licence: MIT", "Copyright (c) 2013-2025 Niels Lohmann")]
    public void The_code_compiled_into_sherpa_onnx_has_an_entry_with_its_licence_and_source(string heading, string licence, string source)
    {
        var parts = Regex.Split(Notices.Replace("\r\n", "\n"), @"(?m)^-{78}$");
        var at = Array.FindIndex(parts, p => p.Trim().StartsWith(heading, StringComparison.Ordinal) && p.Contains("compiled into sherpa-onnx-c-api", StringComparison.Ordinal));
        Assert.True(at > 0 && at + 1 < parts.Length, $"no '{heading.Trim()}' heading in THIRD-PARTY-NOTICES.txt");
        Assert.Contains(licence, parts[at + 1]);
        Assert.Contains(source, parts[at + 1]);
    }

    [Fact]
    public void Eigen_says_where_its_exact_source_is_and_carries_the_MPL_text()
    {
        // MPL-2.0 section 3.2: recipients of the executable form are told where the source form is available.
        var notices = Notices;
        Assert.Contains("Source Code Form: Eigen's source is available at https://gitlab.com/libeigen/eigen/-/archive/5.0.1/eigen-5.0.1.tar.gz", notices);
        Assert.Contains("e9c326dc8c05cd1e044c71f30f1b2e34a6161a3b6ecf445d56b53ff1669e3dec", notices); // sherpa-onnx cmake/eigen.cmake pin
        Assert.Contains("Mozilla Public License Version 2.0", notices);
        Assert.Contains("Minpack Copyright Notice (1999) University of Chicago", notices);
        Assert.Contains("Copyright (c) 2011, Intel Corporation", notices);
    }

    /// <summary>
    /// ONNX Runtime 1.28.2's own ThirdPartyNotices.txt, from the csukuangfj/onnxruntime-libs v1.28.2 archive whose onnxruntime.dll
    /// is the one the win-x64 package carries (identical, with LF endings, to Microsoft's v1.28.2 file), ships beside
    /// THIRD-PARTY-NOTICES.txt in every Windows and macOS build, and THIRD-PARTY-NOTICES.txt points to it.
    /// </summary>
    [Fact]
    public void ONNX_Runtime_notices_are_in_the_repository_ship_beside_THIRD_PARTY_NOTICES_and_are_pointed_to()
    {
        var file = Repo("packaging", "notices", "onnxruntime-ThirdPartyNotices.txt");
        Assert.True(File.Exists(file), $"{file} is missing");
        var text = File.ReadAllText(file).Replace("\r\n", "\n"); // the hash is of the LF form, whatever a checkout does to line endings
        Assert.Equal("0e07b95f3a8d6230037707c5c4a2b554d12c4cb67369669ac255635528ffcee2",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
        Assert.Contains("ships\nunchanged, except for line endings, beside this file as onnxruntime-ThirdPartyNotices.txt", Notices.Replace("\r\n", "\n"));
        var publish = File.ReadAllText(Repo("scripts", "publish.ps1"));
        Assert.Contains(@"Copy-Item -LiteralPath (Join-Path $root ""packaging\notices\onnxruntime-ThirdPartyNotices.txt"") -Destination $out", publish);
        Assert.Contains(@"""THIRD-PARTY-NOTICES.txt"", ""onnxruntime-ThirdPartyNotices.txt""", publish); // in the required list: a publish without it fails
        var mac = File.ReadAllText(Repo("scripts", "publish-mac.sh"));
        Assert.Contains(@"cp packaging/notices/onnxruntime-ThirdPartyNotices.txt ""$resources/onnxruntime-ThirdPartyNotices.txt""", mac);
        Assert.Contains("Contents/Resources/onnxruntime-ThirdPartyNotices.txt; do", mac);
    }
}
