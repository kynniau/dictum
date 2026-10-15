using Microsoft.Win32;

namespace Dictum.Windows.Shell;

/// <summary>Dictum's entry in Settings &gt; Apps as its Inno setup (packaging\Dictum.iss) wrote it, abstracted for tests.</summary>
public interface ISetupRecord
{
    /// <summary>A string value of the entry; null when the entry or the value is missing.</summary>
    string? Get(string name);
}

/// <summary>
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;AppId&gt;_is1. Inno "tacks on _is1" to AppId for the key's
/// name (Inno Setup help, "AppId", read 16/10/2026), and the setup installs per user (PrivilegesRequired=lowest), so the
/// entry is under HKCU. Entries on this PC (VS Code, Inno Setup 7, read 16/10/2026) hold InstallLocation with a trailing
/// backslash and UninstallString as the quoted path of unins000.exe.
/// </summary>
internal sealed class RegistrySetupRecord : ISetupRecord
{
    /// <summary>
    /// packaging\Dictum.iss's AppId with Inno's doubled brace read as one ("two consecutive { characters" stand for one:
    /// Inno Setup help, "Constants", read 16/10/2026). A test parses the .iss, so the two cannot drift apart.
    /// </summary>
    public const string AppId = "{91637A2B-54D2-44E8-A6F4-B7A8319E9E65}";

    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    /// <summary>The program an UninstallString starts (quotes removed), or null when it is not one absolute path.</summary>
    public static string? UninstallerPath(string? command)
    {
        var text = command?.Trim() ?? "";
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0 || end != text.Length - 1) return null; // anything after the quoted path is not just a path
            text = text[1..end];
        }
        return text.Length > 0 && Path.IsPathFullyQualified(text) ? text : null;
    }
}
