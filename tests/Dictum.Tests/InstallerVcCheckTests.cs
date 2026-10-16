using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// The direct-download installer's Visual C++ runtime check (docs/DECISIONS.md, "Visual C++ runtime", lens finding of
/// 17/10/2026). A Setup built from a layout without the runtime (packaging\Dictum.iss under CheckVcRedist) must ask
/// before it installs anything, not after, and must not offer to launch Dictum while the runtime is missing: Dictum then
/// starts but cannot load its speech engine ("model error"). Inno Setup's Pascal script cannot run in a test, so these
/// read packaging\Dictum.iss and scripts\build-installer.ps1 as text; the build proof is ISCC compiling them
/// (scripts\build-installer.ps1), recorded in the DECISIONS entry. Each assertion was observed failing on its reverted text.
/// </summary>
public sealed class InstallerVcCheckTests
{
    private static string Repo(params string[] parts) => Path.Combine([FixBatchDownloadTests.RepoRoot(), .. parts]);

    private static string Iss => File.ReadAllText(Repo("packaging", "Dictum.iss"));

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>A routine's text, from its declaration to the first "end;" in column 0 (every routine in the script ends that way).</summary>
    private static string Routine(string name)
    {
        var match = Regex.Match(Iss, $@"^(function|procedure) {name}\b.*?^end;", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $@"packaging\Dictum.iss has no routine {name}");
        return match.Value;
    }

    [Fact]
    public void The_check_reads_the_redistributables_x64_key_and_its_Installed_Major_and_Minor_values()
    {
        Assert.Contains(@"VcRuntimeKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';", Iss);
        var read = Squash(Routine("VcRuntimeAt"));
        foreach (var value in new[] { "Installed", "Major", "Minor" })
            Assert.Contains($"RegQueryDWordValue(RootKey, VcRuntimeKey, '{value}',", read);
        Assert.Contains("(Installed = 1)", read);
    }

    [Fact]
    public void The_version_test_is_Major_above_the_floor_or_Major_equal_and_Minor_at_or_above()
    {
        Assert.Contains("((Major > {#MinVcMajor}) or ((Major = {#MinVcMajor}) and (Minor >= {#MinVcMinor})))", Squash(Routine("VcRuntimeAt")));
    }

    [Fact]
    public void The_runtime_is_looked_for_in_both_registry_views()
    {
        Assert.Contains("Result := VcRuntimeAt(HKLM64) or VcRuntimeAt(HKLM32);", Squash(Routine("VcRuntimeReady")));
    }

    [Fact]
    public void The_question_is_asked_in_InitializeSetup_before_anything_is_installed_and_nowhere_else()
    {
        var iss = Iss;
        var setup = Squash(Routine("InitializeSetup"));
        Assert.Contains("begin Result := True;", setup); // Setup runs unless the offer is cancelled
        Assert.Contains("if not VcRuntimeReady() then Result := OfferVcRuntime();", setup);
        Assert.Single(Regex.Matches(iss, @"(?<!function )\bOfferVcRuntime\(\)"));
        // The earlier placement was CurStepChanged(ssPostInstall), after the files were copied.
        Assert.DoesNotContain("ssPostInstall", iss);
    }

    [Fact]
    public void Yes_opens_the_download_No_carries_on_and_Cancel_stops_Setup()
    {
        var offer = Squash(Routine("OfferVcRuntime"));
        Assert.Contains("MB_YESNOCANCEL, IDNO)", offer);
        Assert.Contains("if Answer = IDYES then", offer);
        Assert.Contains("ShellExecAsOriginalUser('open', VcRedistUrl,", offer);
        Assert.Contains("else if Answer = IDCANCEL then Result := False;", offer);
        // Yes and No both carry on, so the routine starts true; Cancel is the only path that sets it false.
        Assert.Contains("begin Result := True; Log(", offer);
        Assert.Single(Regex.Matches(offer, @"Result := False;"));
        Assert.Contains("VcRedistUrl = 'https://aka.ms/vc14/vc_redist.x64.exe';", Iss);
    }

    [Fact]
    public void The_question_says_the_PC_has_none_or_one_older_than_the_floor()
    {
        var offer = Squash(Routine("OfferVcRuntime"));
        Assert.Contains("This PC has none, or one older than '", offer);
        Assert.DoesNotContain("does not have it", offer);
    }

    [Fact]
    public void The_launch_checkbox_waits_for_the_runtime_and_is_otherwise_the_same_line()
    {
        var match = Regex.Match(Iss, @"^\[Run\]\r?\n#ifdef CheckVcRedist\r?\n(?:;[^\n]*\n)*(?<checked>Filename: [^\n]*)\r?\n#else\r?\n(?<plain>Filename: [^\n]*)\r?\n#endif", RegexOptions.Multiline);
        Assert.True(match.Success, @"packaging\Dictum.iss: [Run] is not an #ifdef CheckVcRedist pair of Filename lines");
        var withCheck = match.Groups["checked"].Value.TrimEnd();
        var plain = match.Groups["plain"].Value.TrimEnd();
        Assert.EndsWith("; Check: VcRuntimeReady", withCheck);
        Assert.Contains("postinstall", withCheck);
        Assert.Equal(plain, withCheck[..^"; Check: VcRuntimeReady".Length]);
        Assert.DoesNotContain("Check:", plain);
    }

    [Fact]
    public void The_build_script_defines_the_check_and_passes_the_floor_from_MinVcRuntime()
    {
        var script = File.ReadAllText(Repo("scripts", "build-installer.ps1"));
        Assert.Contains(@". (Join-Path $PSScriptRoot ""build-common.ps1"")", script);
        Assert.Contains(@"$defines += @(""/DCheckVcRedist"", ""/DMinVcMajor=$($MinVcRuntime.Major)"", ""/DMinVcMinor=$($MinVcRuntime.Minor)"")", script);
        // Setup refuses to compile with the check but without the floor.
        Assert.Contains("#ifndef MinVcMajor", Iss);
        Assert.Contains("#ifndef MinVcMinor", Iss);
    }
}
