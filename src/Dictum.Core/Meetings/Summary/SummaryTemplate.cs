namespace Dictum.Meetings.Summaries;

public sealed record SummaryTemplate(string Id, string Name, string Markdown);

public static class SummaryTemplates
{
    private static readonly (string Id, string Name)[] Builtins =
    [
        ("general", "General meeting"), ("client-file-note", "Client file note"),
        ("lender-call", "Lender call"), ("stand-up", "Stand-up")
    ];

    public static IReadOnlyList<SummaryTemplate> Load(string? configDirectory = null) =>
        Builtins.Select(entry => LoadOne(entry.Id, entry.Name, configDirectory)).ToArray();

    public static SummaryTemplate Get(string id, string? configDirectory = null)
    {
        var entry = Builtins.FirstOrDefault(entry => entry.Id == id);
        if (entry.Id is null) throw new ArgumentException("Unknown meeting template.", nameof(id));
        return LoadOne(entry.Id, entry.Name, configDirectory);
    }

    private static SummaryTemplate LoadOne(string id, string name, string? configDirectory)
    {
        if (configDirectory is not null)
        {
            try
            {
                var config = RealPath(configDirectory);
                var directory = Path.Combine(config, "meeting-templates");
                var path = RealPath(Path.Combine(directory, id + ".md"));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (path.StartsWith(directory + Path.DirectorySeparatorChar, comparison) && File.Exists(path))
                    return new(id, name, File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // An override that cannot be safely resolved is ignored.
            }
        }
        using var resource = typeof(SummaryTemplates).Assembly.GetManifestResourceStream(
            $"Dictum.Meetings.Summary.Templates.{id}.md")
            ?? throw new InvalidOperationException("Missing built-in meeting template.");
        using var reader = new StreamReader(resource);
        return new(id, name, reader.ReadToEnd());
    }

    private static string RealPath(string path, int depth = 0)
    {
        if (depth > 64) throw new IOException("Too many template links.");
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            var target = entry.ResolveLinkTarget(true) ?? throw new IOException("Unresolved template link.");
            // Resolve target ancestors too, not just a chain on the final component.
            current = RealPath(target.FullName, depth + 1);
        }
        return current;
    }
}
