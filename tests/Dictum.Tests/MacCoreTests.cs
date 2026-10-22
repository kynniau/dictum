using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>
/// The Core changes the macOS build needed (docs/DECISIONS.md "Dictum for macOS", 22/10/2026): the keys' words on a Mac
/// keyboard, the Mac default hotkey, and the clean-up engine recognising its macOS library. Pure, run on every OS, through
/// the overloads that take the dialect; <see cref="HotkeyNames.Dialect"/> itself is global and is only read here.
/// </summary>
public sealed class MacCoreTests
{

    [Theory]
    [InlineData("RightAlt", "Right Alt", "Right Option")]
    [InlineData("Ctrl+Win", "Ctrl+Win", "Ctrl+Command")]
    [InlineData("LeftWin+Space", "Left Win+Space", "Left Command+Space")]
    [InlineData("RightCtrl", "Right Ctrl", "Right Ctrl")] // the same word on both keyboards
    [InlineData("Mouse4", "Mouse 4", "Mouse 4")]
    public void Keys_are_named_for_the_keyboard_in_front_of_the_user(string token, string windows, string mac)
    {
        Assert.Equal(windows, HotkeyNames.Display(token, KeyDialect.Windows));
        Assert.Equal(mac, HotkeyNames.Display(token, KeyDialect.Mac));
        Assert.Equal(windows, HotkeyNames.Display(token)); // the global dialect is Windows in the tests
    }

    [Fact]
    public void The_dialect_never_changes_the_settings_token()
    {
        var spec = HotkeyParser.Parse("RightAlt");
        Assert.Equal("RightAlt", spec.Display); // the parser's own name, what settings.json holds
        Assert.Equal("Right Option", HotkeyNames.Display(spec, KeyDialect.Mac)); // only the words shown change
    }

    [Fact]
    public void A_fresh_settings_file_gets_the_platforms_default_hotkey()
    {
        Assert.Equal("RightCtrl", HotkeyNames.DefaultHotkeyFor(KeyDialect.Windows));
        Assert.Equal("RightAlt", HotkeyNames.DefaultHotkeyFor(KeyDialect.Mac));
        Assert.Equal(HotkeyNames.DefaultHotkey, new AppSettings().Hotkey); // the default follows the platform's dialect
        Assert.Equal(HotkeyNames.DefaultHotkey, SettingsLoader.Parse("{}").Settings.Hotkey);
        Assert.True(HotkeyParser.TryParse(HotkeyNames.DefaultHotkeyFor(KeyDialect.Mac), out _, out _));
        Assert.True(HotkeyParser.TryParse(HotkeyNames.DefaultHotkeyFor(KeyDialect.Windows), out _, out _));
    }

    [Fact]
    public void The_default_dialect_is_Windows_so_no_existing_string_moved()
    {
        Assert.Equal(KeyDialect.Windows, HotkeyNames.Dialect); // nothing in the tests sets it
        Assert.Equal("RightCtrl", HotkeyNames.DefaultHotkey);
    }

    [Theory]
    [InlineData(@"Successfully loaded 'C:\Dictum\runtimes\win-x64\native\vulkan\llama.dll'", @"C:\Dictum\runtimes\win-x64\native\vulkan\llama.dll")]
    [InlineData("Successfully loaded '/Applications/Dictum.app/Contents/MacOS/runtimes/osx-arm64/native/libllama.dylib'", "/Applications/Dictum.app/Contents/MacOS/runtimes/osx-arm64/native/libllama.dylib")]
    [InlineData("Successfully loaded '/x/libggml-metal.dylib'", null)] // not the llama library
    public void The_loaded_clean_up_library_is_recognised_on_both_platforms(string line, string? expected) =>
        Assert.Equal(expected, LocalLlmEngine.LoadedLibraryPath(line));
}
