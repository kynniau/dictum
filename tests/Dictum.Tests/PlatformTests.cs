using System.Reflection;
using Dictum.Calibration;
using Dictum.Hotkey;
using Dictum.Personal;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Windows;
using Dictum.Windows.Shell;

namespace Dictum.Tests;

/// <summary>SPEC §21: IPlatform is the only seam. Dictum.Core and Dictum.UI reference no platform assembly and call no native API.</summary>
public class ArchitectureTests
{
    private static readonly string[] PlatformAssemblies =
    [
        "Dictum", "Dictum.Windows", "Dictum.Mac", "Avalonia.Win32", "Avalonia.Win32.Automation", "Avalonia.Native", "Avalonia.X11", "Avalonia.Desktop",
        "Avalonia.FreeDesktop", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Drawing.Common",
        "NAudio.Wasapi", "NAudio.WinMM", "NAudio.WinForms", "Microsoft.Windows.SDK.NET", "WinRT.Runtime", "Microsoft.Win32.Registry",
        "Microsoft.Win32.SystemEvents", "Microsoft.Windows.Compatibility",
    ];

    public static TheoryData<string> SharedAssemblies => ["Dictum.Core", "Dictum.UI"];

    private static Assembly Load(string name) => name == "Dictum.Core" ? typeof(AppSettings).Assembly : typeof(Dictum.UI.App).Assembly;

    [Theory]
    [MemberData(nameof(SharedAssemblies))]
    public void Shared_code_references_no_platform_assembly(string name)
    {
        var references = Load(name).GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Empty(references.Intersect(PlatformAssemblies, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(SharedAssemblies))]
    public void Shared_code_declares_no_native_call(string name)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var native = Load(name).GetTypes().SelectMany(t => t.GetMethods(all))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0 || m.GetCustomAttributes().Any(a => a.GetType().Name is "DllImportAttribute" or "LibraryImportAttribute"))
            .Select(m => $"{m.DeclaringType}.{m.Name}").ToList();
        Assert.Empty(native);
    }

    [Fact]
    public void The_windows_platform_is_the_seam_and_names_its_mechanisms()
    {
        TestEnv.WindowsOnly("Constructs the Windows platform, which calls Win32 and reads the HKCU Run key.");
        using var platform = new WindowsPlatform(NullLog.Instance, AppContext.BaseDirectory);
        Assert.Equal("Windows", platform.Name);
        Assert.False(platform.Package.IsPackaged); // a test run is never packaged
        Assert.Contains("Run key", platform.Startup.Mechanism, StringComparison.OrdinalIgnoreCase); // unpackaged: the HKCU Run key
    }
}

/// <summary>SPEC §18.2: packaged-mode detection and what it changes, and the program-folder removal's refusals.</summary>
public class PackagingTests
{
    [Fact]
    public void Storage_shows_the_redirected_folder_of_a_packaged_build()
    {
        using var scratch = new Scratch();
        var local = Path.Combine(scratch.Root.FullName, "local");
        var roaming = Path.Combine(scratch.Root.FullName, "roaming");
        var settings = Path.Combine(roaming, "Dictum");
        var redirected = Path.Combine(local, "Packages", "Kynni.Dictum_test", "LocalCache", "Roaming", "Dictum");
        Directory.CreateDirectory(redirected);

        var unpackaged = new PackageInfo(false, null, null, local, roaming);
        Assert.Equal(settings, unpackaged.RealPath(settings));
        var packaged = new PackageInfo(true, "Kynni.Dictum_1.0.0.0_x64__test", "Kynni.Dictum_test", local, roaming);
        Assert.Equal(redirected, packaged.RealPath(settings));
        Assert.Equal(Path.Combine(local, "Dictum"), packaged.RealPath(Path.Combine(local, "Dictum"))); // no redirected copy: the real folder is used
    }

    [Fact]
    public void Each_build_warns_when_the_other_one_is_installed_too()
    {
        using var scratch = new Scratch();
        var local = Path.Combine(scratch.Root.FullName, "local");
        var roaming = Path.Combine(scratch.Root.FullName, "roaming");
        Assert.Null(new PackageInfo(false, null, null, local, roaming).OtherInstall());
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Kynni.Dictum_43s584kx66f64"));
        Assert.Contains("Microsoft Store", new PackageInfo(false, null, null, local, roaming).OtherInstall());

        Assert.Null(new PackageInfo(true, "x", "Kynni.Dictum_43s584kx66f64", local, roaming).OtherInstall());
        Directory.CreateDirectory(Path.Combine(local, "Programs", "Dictum"));
        File.WriteAllText(Path.Combine(local, "Programs", "Dictum", "Dictum.exe"), "");
        Assert.Contains("direct-download", new PackageInfo(true, "x", "Kynni.Dictum_43s584kx66f64", local, roaming).OtherInstall());
    }

    [Fact]
    public void The_program_folder_is_removed_only_when_it_is_an_installed_Dictum()
    {
        TestEnv.WindowsOnly("Runs the detached removal step in Windows PowerShell.");
        using var scratch = new Scratch();
        var install = Directory.CreateDirectory(Path.Combine(scratch.Root.FullName, "Programs", "Dictum")).FullName;
        Assert.Contains("does not hold Dictum.exe", RemovalSteps.Refusal(install, packaged: false));
        File.WriteAllText(Path.Combine(install, "Dictum.exe"), "");
        Assert.Contains("no manifest.txt", RemovalSteps.Refusal(install, packaged: false)); // SPEC §17, amended: never a folder wholesale
        File.WriteAllText(Path.Combine(install, "manifest.txt"), "Dictum.exe\t0\tabc\n");
        Assert.Null(RemovalSteps.Refusal(install, packaged: false));
        Assert.Contains("Settings > Apps", RemovalSteps.Refusal(install, packaged: true)); // a package cannot delete itself
        Assert.NotNull(RemovalSteps.Refusal(Path.GetPathRoot(install)!, packaged: false));
        File.WriteAllText(Path.Combine(scratch.Root.FullName, "Dictum.sln"), "");
        Assert.Contains("source tree", RemovalSteps.Refusal(install, packaged: false));

        File.Delete(Path.Combine(scratch.Root.FullName, "Dictum.sln"));
        var steps = new RemovalSteps(new FakeRunKey(), install, Path.Combine(install, "Dictum.exe"), false, scratch.Root.FullName, scratch.Root.FullName, NullLog.Instance,
            waitForProcessId: 1234);
        var start = steps.RemovalStart(install);
        Assert.Equal(install, start.Environment[RemovalSteps.FolderVariable]); // the folder travels outside the script text
        Assert.Equal("1234", start.Environment[RemovalSteps.WaitVariable]);
        Assert.Equal("manifest", start.Environment[RemovalSteps.ModeVariable]);
        Assert.DoesNotContain(install, RemovalSteps.Script);
        Assert.Contains("Join-Path $f 'manifest.txt'", RemovalSteps.Script); // only what the manifest lists, read just before deleting
        Assert.DoesNotContain("-Recurse", RemovalSteps.Script); // never a folder wholesale
    }
}

/// <summary>SPEC §20: the "Press your hotkey" logic, apart from its window.</summary>
public class HotkeyCaptureTests
{
    private static RawKeyEvent Down(int vk) => new(vk, true, false);

    private static RawKeyEvent Up(int vk) => new(vk, false, false);

    [Fact]
    public void A_single_key_is_named_with_its_side()
    {
        var capture = new HotkeyCapture();
        Assert.Null(capture.Feed(Down(Vk.RMenu)));
        Assert.Equal("Right Alt down", capture.LastReceived); // the display name says the side (HotkeyNames, 1.2 polish)
        Assert.True(capture.IsHeld(HotkeyParser.Parse("RightAlt")));
        Assert.False(capture.IsHeld(HotkeyParser.Parse("RightCtrl")));
        Assert.Equal("RightAlt", capture.Feed(Up(Vk.RMenu)));
        Assert.False(capture.IsHeld(HotkeyParser.Parse("RightAlt")));
    }

    [Fact]
    public void A_chord_lists_its_modifiers_first_and_Esc_alone_names_nothing()
    {
        var capture = new HotkeyCapture();
        capture.Feed(Down(Vk.LControl));
        capture.Feed(Down(Vk.LShift));
        capture.Feed(Down(Vk.Space));
        Assert.True(capture.IsHeld(HotkeyParser.Parse("Ctrl+Shift+Space")));
        capture.Feed(Up(Vk.Space));
        capture.Feed(Up(Vk.LShift));
        Assert.Equal("LeftCtrl+LeftShift+Space", capture.Feed(Up(Vk.LControl)));
        Assert.Null(capture.Feed(Down(Vk.Escape)));
        Assert.Equal("Esc down", capture.LastReceived);
        Assert.Null(capture.Feed(Up(Vk.Escape))); // the parser refuses Esc: it cancels a dictation
    }
}

/// <summary>Smaller pieces of the polish lane.</summary>
public class PolishLaneTests
{
    [Fact]
    public void The_model_path_variable_has_a_new_name_and_the_old_one_still_works()
    {
        Assert.Equal(@"C:\a.bin", SettingsFile.ModelPathOverride(v => v == "DICTUM_MODEL_PATH" ? @"C:\a.bin" : v == "DICTATE_MODEL_PATH" ? @"C:\b.bin" : null));
        Assert.Equal(@"C:\b.bin", SettingsFile.ModelPathOverride(v => v == "DICTATE_MODEL_PATH" ? @"C:\b.bin" : null));
        Assert.Null(SettingsFile.ModelPathOverride(_ => null));
    }

    [Fact]
    public void Calibration_results_show_the_raw_error_count_beside_the_rate()
    {
        var combo = new ComboResult("small.en", 0, [new ClipOutcome(1, "", 1, 10, new WerResult(3, 40, [])), new ClipOutcome(2, "", 1, 10, new WerResult(1, 60, []))]);
        Assert.Equal(4, combo.Edits);
        Assert.Equal(100, combo.Words);
        Assert.Equal($"{0.04:P1} (4 of 100 words)", combo.ErrorsText);
    }

    [Fact]
    public void Within_one_point_of_the_best_the_faster_decoding_is_recommended()
    {
        var beam = CalibrationRankingTests.Combo("large-v3-turbo", 5, 0.040, secondsPerClip: 2.0);
        var greedy = CalibrationRankingTests.Combo("large-v3-turbo", 0, 0.050, secondsPerClip: 0.8); // one point worse, much faster
        var worse = CalibrationRankingTests.Combo("small.en", 0, 0.070, secondsPerClip: 0.4); // three points worse: never
        Assert.Same(greedy, CalibrationRanking.Recommend([beam, greedy, worse]));
        var farther = CalibrationRankingTests.Combo("large-v3-turbo", 0, 0.060, secondsPerClip: 0.8); // two points: the more accurate wins
        Assert.Same(beam, CalibrationRanking.Recommend([beam, farther, worse]));
    }

    [Fact]
    public void A_number_written_differently_is_flagged_as_probably_a_one_off()
    {
        Assert.True(CorrectionSuggestions.LikelyOneOff(new CorrectionRule("fourteen", "14th")));
        Assert.False(CorrectionSuggestions.LikelyOneOff(new CorrectionRule("Kinny", "Kynni")));
    }

    [Fact]
    public void The_controller_sends_states_when_only_a_big_indicator_is_on()
    {
        Assert.False(new AppSettings { Pill = new PillSettings { Enabled = false } }.ShowsDictationState);
        Assert.True(new AppSettings { Pill = new PillSettings { Enabled = false }, Indicator = new IndicatorSettings { Mode = IndicatorSettings.Orb } }.ShowsDictationState);
        Assert.True(new AppSettings { Pill = new PillSettings { Enabled = false }, Indicator = new IndicatorSettings { Mode = IndicatorSettings.Edge } }.ShowsDictationState);
        Assert.True(new AppSettings().ShowsDictationState);
    }

    [Fact]
    public void Indicator_and_sound_settings_load_with_safe_defaults()
    {
        var defaults = SettingsLoader.Parse("{ }").Settings;
        Assert.Equal(IndicatorSettings.Off, defaults.Indicator.Mode);
        Assert.Equal("bottom-right", defaults.Indicator.OrbCorner);
        Assert.False(defaults.Sounds.Enabled);
        var set = SettingsLoader.Parse("""{ "indicator": { "mode": "both", "orb_corner": "top-left" }, "sounds": { "enabled": true } }""").Settings;
        Assert.True(set.Indicator.ShowsOrb && set.Indicator.ShowsEdge);
        Assert.Equal("top-left", set.Indicator.OrbCorner);
        Assert.True(set.Sounds.Enabled);
        var bad = SettingsLoader.Parse("""{ "indicator": { "mode": "huge" } }""");
        Assert.Equal(IndicatorSettings.Off, bad.Settings.Indicator.Mode);
        Assert.NotEmpty(bad.Warnings);
    }
}
