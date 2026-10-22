using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dictum.Tests;

/// <summary>
/// Store certification, 16/10/2026: every whisper.cpp and llama.cpp DLL imports the Visual C++ runtime (msvcp140.dll,
/// vcruntime140.dll and vcruntime140_1.dll; the CPU builds also vcomp140.dll), which a clean Windows does not have, so
/// neither engine loaded on the tester's PC. The Store package must declare Microsoft's VC++ framework package and carry
/// no copy of its own; a direct-download layout must have the DLLs beside every native DLL that imports them, because
/// LLamaSharp does not look beside Dictum.exe (docs/DECISIONS.md, "Visual C++ runtime"). The layout read is
/// DICTUM_PUBLISHED_DIR when that test runs (a publish.ps1 output, or an MSIX layout with its AppxManifest.xml), else this
/// test run's own output, whose runtimes folder comes from the same NuGet packages as the published one.
/// </summary>
public sealed class VcRuntimeTests : IDisposable
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-vcrt-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static string BuildOutput => AppContext.BaseDirectory;

    private static string Template =>
        File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "packaging", "AppxManifest.template.xml"));

    [Fact]
    public void Every_whisper_and_llama_runtime_imports_the_VC_runtime_and_only_the_DLLs_publish_copies()
    {
        TestEnv.WindowsOnly("Reads the Windows x64 native DLLs' imports of the Visual C++ runtime.");
        var importers = VcRuntimeLayout.Importers(BuildOutput);
        var paths = importers.Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] engines =
        [
            @"runtimes\win-x64\whisper.dll", @"runtimes\noavx\win-x64\whisper.dll", @"runtimes\vulkan\win-x64\whisper.dll",
            .. new[] { "avx", "avx2", "avx512", "noavx", "vulkan" }.Select(level => $@"runtimes\win-x64\native\{level}\llama.dll"),
        ];
        Assert.All(engines, engine => Assert.Contains(engine, paths));
        // publish.ps1 copies $VcRuntimeDlls (scripts\build-common.ps1) and nothing else: a new import must be added there.
        Assert.All(importers, i => Assert.Subset(VcRuntimeLayout.Shipped, i.VcDlls));
        Assert.Contains(importers, i => i.VcDlls.Contains("vcomp140.dll"));
    }

    [Fact]
    public void The_Store_package_declares_Microsofts_VC_runtime_framework_and_carries_no_copy() =>
        Assert.Empty(VcRuntimeLayout.StoreProblems(BuildOutput, Template));

    [Theory]
    [InlineData("missing", "declares no Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("older", "older than 14.0.33728.0")]
    [InlineData("publisher", "not Microsoft's")]
    public void A_Store_manifest_without_the_tested_framework_from_Microsoft_is_refused(string change, string expected)
    {
        var manifest = XDocument.Parse(Template);
        var dependency = manifest.Descendants(Foundation + "PackageDependency").Single(d => (string?)d.Attribute("Name") == VcRuntimeLayout.Framework);
        switch (change)
        {
            case "missing": dependency.Remove(); break;
            case "older": dependency.SetAttributeValue("MinVersion", "14.0.30704.0"); break;
            default: dependency.SetAttributeValue("Publisher", "CN=Contoso"); break;
        }
        Assert.Contains(expected, Assert.Single(VcRuntimeLayout.StoreProblems(BuildOutput, manifest.ToString())));
    }

    [Fact]
    public void A_Store_layout_carrying_its_own_VC_runtime_is_refused()
    {
        TestEnv.WindowsOnly("Reads the Windows x64 native DLLs' imports of the Visual C++ runtime.");
        var copy = Path.Combine(_dir.FullName, @"runtimes\win-x64\native\avx2\vcruntime140.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, []);
        Assert.Contains(@"runtimes\win-x64\native\avx2\vcruntime140.dll", Assert.Single(VcRuntimeLayout.StoreProblems(_dir.FullName, Template)));
    }

    [Fact]
    public void A_direct_download_layout_needs_the_VC_runtime_beside_every_native_DLL_that_imports_it()
    {
        TestEnv.WindowsOnly("Reads the Windows x64 native DLLs' imports of the Visual C++ runtime.");
        // publish.ps1's rule, the four DLLs in every folder under runtimes\ that holds a DLL, applied to a scratch tree
        // shaped like this run's output; the check looks there for each native DLL's runtime instead of beside it.
        var folders = Directory.EnumerateFiles(Path.Combine(BuildOutput, "runtimes"), "*.dll", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(BuildOutput, Path.GetDirectoryName(f)!)).Distinct();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(_dir.FullName, folder));
            foreach (var dll in VcRuntimeLayout.Shipped) File.WriteAllBytes(Path.Combine(_dir.FullName, folder, dll), []);
        }
        Assert.Empty(VcRuntimeLayout.AppLocalProblems(BuildOutput, _dir.FullName));

        File.Delete(Path.Combine(_dir.FullName, @"runtimes\win-x64\native\avx2\vcomp140.dll"));
        var problem = Assert.Single(VcRuntimeLayout.AppLocalProblems(BuildOutput, _dir.FullName));
        Assert.Equal(@"runtimes\win-x64\native\avx2\ggml-cpu.dll imports vcomp140.dll, which is not beside it", problem);
    }

    [Fact]
    public void The_oldest_VC_runtime_the_scripts_accept_is_no_older_than_the_newest_linker_of_a_native_DLL()
    {
        var newest = VcRuntimeLayout.Importers(BuildOutput).Max(i => i.Linker)!;
        Assert.True(VcRuntimeLayout.MinVcRuntime >= newest,
            $"scripts\\build-common.ps1 accepts a Visual C++ runtime from {VcRuntimeLayout.MinVcRuntime}, but a native DLL was linked by MSVC {newest}");
    }

    /// <summary>
    /// Gated on DICTUM_PUBLISHED_DIR: a real layout as it will ship. A folder with an AppxManifest.xml (an MSIX layout,
    /// packaging\out\msix-store\layout) is checked as the Store package; any other (dist\Dictum from publish.ps1
    /// -VcRuntimeDir) must carry the runtime beside every native DLL.
    /// </summary>
    [Fact]
    public void A_published_layout_gives_every_native_DLL_its_VC_runtime()
    {
        var layout = TestEnv.Get("PUBLISHED_DIR");
        Assert.SkipWhen(layout is null, @"DICTUM_PUBLISHED_DIR is not set (dist\Dictum from publish.ps1 -VcRuntimeDir, or packaging\out\msix-store\layout).");
        var manifest = Path.Combine(layout, "AppxManifest.xml");
        Assert.NotEmpty(VcRuntimeLayout.Importers(layout));
        Assert.Empty(File.Exists(manifest)
            ? VcRuntimeLayout.StoreProblems(layout, File.ReadAllText(manifest))
            : VcRuntimeLayout.AppLocalProblems(layout, layout));
    }
}

/// <summary>
/// Reads a Dictum layout for <see cref="VcRuntimeTests"/>. The DLL list and the oldest runtime accepted come from
/// scripts\build-common.ps1, so the tests check what the build scripts use.
/// </summary>
internal static partial class VcRuntimeLayout
{
    public const string Framework = "Microsoft.VCLibs.140.00.UWPDesktop";
    public const string MicrosoftPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    /// <summary>
    /// The framework version tested on 16/10/2026: its runtime (14.40.33728) exports every function the native DLLs import,
    /// and both engines loaded with those DLLs on a clean Windows Sandbox.
    /// </summary>
    public static readonly Version TestedFramework = new(14, 0, 33728, 0);

    /// <summary>The DLLs publish.ps1 copies beside the native runtimes ($VcRuntimeDlls).</summary>
    public static HashSet<string> Shipped =>
        [.. BuildCommon(@"\$VcRuntimeDlls = @\(([^)]*)\)").Split(',').Select(name => name.Trim().Trim('"').ToLowerInvariant())];

    /// <summary>The oldest Visual C++ runtime the scripts accept ($MinVcRuntime).</summary>
    public static Version MinVcRuntime => Version.Parse(BuildCommon(@"\$MinVcRuntime = \[version\]""([\d.]+)"""));

    public sealed record Importer(string RelativePath, Version Linker, HashSet<string> VcDlls);

    /// <summary>A Visual C++ runtime DLL ("Determine which DLLs to redistribute", Microsoft Learn); not .NET's vcruntime140_cor3.dll.</summary>
    public static bool IsVcRuntime(string fileName) => VcName().IsMatch(fileName);

    /// <summary>Every x64 DLL under the layout's runtimes folder that imports the Visual C++ runtime.</summary>
    public static List<Importer> Importers(string layout)
    {
        var importers = new List<Importer>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(layout, "runtimes"), "*.dll", SearchOption.AllDirectories))
        {
            if (IsVcRuntime(Path.GetFileName(path))) continue;
            var pe = PeFile.Read(path);
            var vc = pe.Imports.Where(IsVcRuntime).Select(dll => dll.ToLowerInvariant()).ToHashSet();
            if (pe.Machine == PeFile.Amd64 && vc.Count > 0) importers.Add(new(Path.GetRelativePath(layout, path), pe.Linker, vc));
        }
        return importers;
    }

    /// <summary>What would stop the Store package's native DLLs loading on a PC without the runtime: empty when nothing would.</summary>
    public static List<string> StoreProblems(string layout, string manifestXml)
    {
        var problems = new List<string>();
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var dependency = XDocument.Parse(manifestXml).Root?.Element(ns + "Dependencies")?.Elements(ns + "PackageDependency")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == Framework);
        var importers = Importers(layout);
        if (importers.Count > 0 && dependency is null)
        {
            problems.Add($"{importers.Count} native DLLs import the Visual C++ runtime (first: {importers[0].RelativePath}), but the " +
                $"manifest declares no {Framework} PackageDependency, so a PC without the runtime cannot load them");
        }
        else if (importers.Count > 0)
        {
            var publisher = (string?)dependency!.Attribute("Publisher");
            if (publisher != MicrosoftPublisher) problems.Add($"{Framework}: Publisher \"{publisher}\" is not Microsoft's \"{MicrosoftPublisher}\"");
            var minVersion = (string?)dependency.Attribute("MinVersion");
            if (!Version.TryParse(minVersion, out var min) || min < TestedFramework)
                problems.Add($"{Framework}: MinVersion \"{minVersion}\" is older than {TestedFramework}, the version tested");
        }
        problems.AddRange(Directory.EnumerateFiles(layout, "*.dll", SearchOption.AllDirectories)
            .Where(path => IsVcRuntime(Path.GetFileName(path)))
            .Select(path => $"{Path.GetRelativePath(layout, path)}: a packaged app takes the Visual C++ runtime from {Framework}, " +
                "never from a copy of its own (KB 3176696)"));
        return problems;
    }

    /// <summary>
    /// What would stop a direct-download layout's native DLLs loading on a PC without the runtime: each needs the runtime
    /// DLLs it imports in its own folder. <paramref name="vcRoot"/> is where they are looked for, at each native DLL's
    /// relative folder: the layout itself, or a scratch tree in a test.
    /// </summary>
    public static List<string> AppLocalProblems(string layout, string vcRoot) =>
        [.. from importer in Importers(layout)
            from dll in importer.VcDlls.Order()
            where !File.Exists(Path.Combine(vcRoot, Path.GetDirectoryName(importer.RelativePath)!, dll))
            select $"{importer.RelativePath} imports {dll}, which is not beside it"];

    private static string BuildCommon(string pattern)
    {
        var match = Regex.Match(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "scripts", "build-common.ps1")), pattern);
        Assert.True(match.Success, $"scripts\\build-common.ps1 has no line matching {pattern}");
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"^(msvcp140(_\w+)?|vcruntime140(_1)?|vcomp140|concrt140)\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex VcName();
}

/// <summary>
/// A PE file's machine, linker version and imported DLL names (import and delay-import tables), read without loading it:
/// a C# port of the investigation's pe-imports.ps1 (16/10/2026) that reads only the headers and the import tables.
/// </summary>
internal sealed record PeFile(ushort Machine, Version Linker, IReadOnlyList<string> Imports)
{
    public const ushort Amd64 = 0x8664;

    public static PeFile Read(string path)
    {
        using var file = File.OpenHandle(path);
        var length = RandomAccess.GetLength(file);
        byte[] At(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > length) throw new InvalidDataException($"{path}: bytes {offset}+{count} lie outside the file.");
            var bytes = new byte[count];
            for (var done = 0; done < count;)
            {
                var read = RandomAccess.Read(file, bytes.AsSpan(done), offset + done);
                if (read == 0) throw new InvalidDataException($"{path}: the file ended early.");
                done += read;
            }
            return bytes;
        }
        int Int32(long offset) => BitConverter.ToInt32(At(offset, 4));

        if (BitConverter.ToUInt16(At(0, 2)) != 0x5A4D) throw new InvalidDataException($"{path} is not a PE file (no MZ header).");
        long pe = Int32(0x3C);
        if (Int32(pe) != 0x4550) throw new InvalidDataException($"{path} has no PE signature.");
        var fileHeader = At(pe + 4, 20);
        var machine = BitConverter.ToUInt16(fileHeader, 0);
        var sectionCount = BitConverter.ToUInt16(fileHeader, 2);
        var optional = pe + 24;
        var header = At(optional, BitConverter.ToUInt16(fileHeader, 16));
        var pe32Plus = BitConverter.ToUInt16(header, 0) == 0x20B;
        var linker = new Version(header[2], header[3]);
        var directoryCount = BitConverter.ToInt32(header, pe32Plus ? 108 : 92);
        int DataDirectory(int index)
        {
            var at = (pe32Plus ? 112 : 96) + 8 * index;
            return index < directoryCount && at + 4 <= header.Length ? BitConverter.ToInt32(header, at) : 0;
        }
        var sections = At(optional + header.Length, 40 * sectionCount);
        long Offset(int rva)
        {
            for (var s = 0; s < 40 * sectionCount; s += 40)
            {
                var size = Math.Max(BitConverter.ToInt32(sections, s + 8), BitConverter.ToInt32(sections, s + 16));
                var address = BitConverter.ToInt32(sections, s + 12);
                if (rva >= address && rva < address + size) return (long)rva - address + BitConverter.ToInt32(sections, s + 20);
            }
            throw new InvalidDataException($"{path}: RVA 0x{rva:X} lies in no section.");
        }
        string Name(int rva)
        {
            var offset = Offset(rva);
            var bytes = At(offset, (int)Math.Min(256, length - offset));
            var end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) throw new InvalidDataException($"{path}: the name at RVA 0x{rva:X} is not terminated.");
            return Encoding.ASCII.GetString(bytes, 0, end);
        }

        var imports = new List<string>();
        // Import descriptors are 20 bytes with the DLL name's RVA at +12; delay-load descriptors are 32 bytes with it at +4.
        // Each table ends with a descriptor whose name RVA is 0.
        foreach (var (directory, size, nameAt) in new[] { (1, 20, 12), (13, 32, 4) })
        {
            var table = DataDirectory(directory);
            if (table == 0) continue;
            for (var o = Offset(table); ; o += size)
            {
                var name = Int32(o + nameAt);
                if (name == 0) break;
                imports.Add(Name(name));
            }
        }
        return new(machine, linker, imports);
    }
}
