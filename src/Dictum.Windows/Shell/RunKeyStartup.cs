using Dictum.Platform;
using Microsoft.Win32;

namespace Dictum.Windows.Shell;

/// <summary>The HKCU Run key, abstracted so tests never touch the real registry.</summary>
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run — only used by "Launch at login" in the unpackaged build.</summary>
public sealed class RegistryRunKey : IRunKey
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>"Launch at login" for the unpackaged build: Run\Dictum = quoted exe path (SPEC §8).</summary>
public sealed class RunKeyStartup(IRunKey runKey, string exePath) : IStartupRegistration
{
    public const string ValueName = "Dictum";

    public string Mechanism => @"Windows Run key (HKCU\…\Run\Dictum)";

    public string Command => $"\"{exePath}\"";

    /// <summary>True when the Run value points at this exe (a stale path from a moved install counts as off).</summary>
    public bool IsEnabled()
    {
        var value = runKey.Get(ValueName);
        return value is not null && string.Equals(value.Trim().Trim('"'), exePath, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) runKey.Set(ValueName, Command);
        else runKey.Delete(ValueName);
    }

    public Task<string?> SetEnabledAsync(bool enabled)
    {
        SetEnabled(enabled);
        return Task.FromResult<string?>(null);
    }
}
