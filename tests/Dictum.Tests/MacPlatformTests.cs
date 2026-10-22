using Dictum.Mac;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// The macOS platform's pure parts (src/Dictum.Mac/MacPlatform.cs; docs/DECISIONS.md "Dictum for macOS", 22/10/2026): where
/// it keeps things and what it says it is. No native call, so these run on every OS; the members that reach macOS are
/// covered by the Mac*Tests files of their lanes.
/// </summary>
public sealed class MacPlatformTests
{
    [Fact]
    public void Both_roots_are_the_application_support_folder_of_the_home_in_HOME()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var scratch = Path.Combine(Path.GetTempPath(), "dictum-mac-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("HOME", scratch);
            using var platform = new MacPlatform(NullLog.Instance, Path.Combine(scratch, "exe"));
            var paths = platform.ResolvePaths(null);
            var support = Path.Combine(scratch, "Library", "Application Support", AppPaths.AppFolder);
            Assert.Equal(support, paths.ConfigDir);
            Assert.Equal(support, paths.LocalDir);
            Assert.Equal(Path.Combine(support, "models"), paths.DefaultModelsDir);
            Assert.Equal(Path.Combine(support, "settings.json"), paths.SettingsFile);
            Assert.False(paths.ConfigOverridden);

            var overridden = platform.ResolvePaths(Path.Combine(scratch, "cfg"));
            Assert.Equal(Path.Combine(scratch, "cfg"), overridden.ConfigDir);
            Assert.Equal(support, overridden.LocalDir); // models and logs stay in the one folder
            Assert.True(overridden.ConfigOverridden);

            var legacy = platform.LegacyFolders();
            Assert.False(Directory.Exists(legacy.ConfigDir)); // there was never a Mac build of the earlier app
            Assert.Equal(Path.Combine(scratch, "Library", "Application Support", AppPaths.LegacyAppFolder), legacy.ConfigDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", home);
        }
    }

    [Fact]
    public void The_mac_platform_names_itself_and_is_never_a_package()
    {
        using var platform = new MacPlatform(NullLog.Instance, Path.GetTempPath());
        Assert.Equal("macOS", platform.Name);
        Assert.False(platform.Package.IsPackaged);
        Assert.Null(platform.Package.OtherInstall());
        Assert.Equal("/x/y", platform.Package.RealPath("/x/y"));
        Assert.False(platform.EarlierBuildRunning());
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Dictum"), platform.ExePath);
        Assert.Null(platform.Bundle); // a temp folder is not Dictum.app/Contents/MacOS
    }
}
