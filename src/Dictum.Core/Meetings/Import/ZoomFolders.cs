namespace Dictum.Meetings.Import;

public static class ZoomFolders
{
    public static string? Default()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return null;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Zoom");
        return Directory.Exists(folder) ? folder : null;
    }
}
