namespace Dictum.Windows.Shell;

/// <summary>
/// Windows' own programs by full path under %SystemRoot%, never by bare name: a bare name is looked up in the running
/// exe's folder first, so a powershell.exe placed beside Dictum.exe ran instead (lens probe G, 16/10/2026).
/// </summary>
internal static class SystemPrograms
{
    /// <summary>
    /// The Windows folder as Windows reports it (the parent of GetSystemDirectory; C:\WINDOWS on this PC), never the
    /// SystemRoot variable: a parent process can set that and steer which powershell.exe runs (lens, 16/10/2026).
    /// </summary>
    public static string? Root => Path.GetDirectoryName(Environment.SystemDirectory);

    /// <summary><paramref name="relative"/> under <paramref name="systemRoot"/>, or null unless that is an absolute folder holding the file.</summary>
    public static string? Find(string? systemRoot, string relative) =>
        systemRoot is { Length: > 0 } root && Path.IsPathFullyQualified(root) && Path.Combine(root, relative) is var path && File.Exists(path) ? path : null;

    /// <summary>Windows PowerShell 5.1 at its standard path (there on this PC, 16/10/2026).</summary>
    public static string? WindowsPowerShell(string? systemRoot) => Find(systemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
}
