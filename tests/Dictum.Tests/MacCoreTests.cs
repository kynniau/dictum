using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// The Core changes the macOS build needed (docs/DECISIONS.md "Dictum for macOS", 22/10/2026): the keys' words on a Mac
/// keyboard, the Mac default hotkey, and the clean-up engine recognising its macOS library. Pure, run on every OS; the
/// dialect is global, so each test restores it.
/// </summary>
[Collection("HotkeyDialect")]
public sealed class MacCoreTests
{
    private static T WithDialect<T>(KeyDialect dialect, Func<T> read)
    {
        var before = HotkeyNames.Dialect;
        HotkeyNames.Dialect = dialect;
        try
        {
            return read();
        }
        finally
        {
            HotkeyNames.Dialect = before;
        }
    }

    [Theory]
    [InlineData("RightAlt", "Right Alt", "Right Option")]
    [InlineData("Ctrl+Win", "Ctrl+Win", "Ctrl+Command")]
    [InlineData("LeftWin+Space", "Left Win+Space", "Left Command+Space")]
    [InlineData("RightCtrl", "Right Ctrl", "Right Ctrl")] // the same word on both keyboards
    [InlineData("Mouse4", "Mouse 4", "Mouse 4")]
    public void Keys_are_named_for_the_keyboard_in_front_of_the_user(string token, string windows, string mac)
    {
        Assert.Equal(windows, WithDialect(KeyDialect.Windows, () => HotkeyNames.Display(token)));
        Assert.Equal(mac, WithDialect(KeyDialect.Mac, () => HotkeyNames.Display(token)));
    }

    [Fact]
    public void The_dialect_never_changes_the_settings_token()
    {
        var spec = WithDialect(KeyDialect.Mac, () => HotkeyParser.Parse("RightAlt"));
        Assert.Equal("RightAlt", spec.Display); // the parser's own name, what settings.json holds
    }

    [Fact]
    public void A_fresh_settings_file_gets_the_platforms_default_hotkey()
    {
        Assert.Equal("RightCtrl", WithDialect(KeyDialect.Windows, () => new AppSettings().Hotkey));
        Assert.Equal("RightAlt", WithDialect(KeyDialect.Mac, () => new AppSettings().Hotkey));
        Assert.Equal("RightAlt", WithDialect(KeyDialect.Mac, () => SettingsLoader.Parse("{}").Settings.Hotkey));
        Assert.True(HotkeyParser.TryParse(HotkeyNames.DefaultHotkey, out _, out _));
    }

    [Fact]
    public void The_default_dialect_is_Windows_so_no_existing_string_moved()
    {
        Assert.Equal(KeyDialect.Windows, WithDialect(KeyDialect.Windows, () => HotkeyNames.Dialect));
    }

    [Theory]
    [InlineData(@"Successfully loaded 'C:\Dictum\runtimes\win-x64\native\vulkan\llama.dll'", @"C:\Dictum\runtimes\win-x64\native\vulkan\llama.dll")]
    [InlineData("Successfully loaded '/Applications/Dictum.app/Contents/MacOS/runtimes/osx-arm64/native/libllama.dylib'", "/Applications/Dictum.app/Contents/MacOS/runtimes/osx-arm64/native/libllama.dylib")]
    [InlineData("Successfully loaded '/x/libggml-metal.dylib'", null)] // not the llama library
    public void The_loaded_clean_up_library_is_recognised_on_both_platforms(string line, string? expected) =>
        Assert.Equal(expected, LocalLlmEngine.LoadedLibraryPath(line));
}
