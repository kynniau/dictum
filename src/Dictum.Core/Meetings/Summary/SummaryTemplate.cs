namespace Dictum.Meetings.Summaries;

public sealed record SummaryTemplate(string Id, string Name, string Markdown)
{
    public string Instructions { get; init; } = "";
    public bool IsBuiltin { get; init; } = true;
}

public static class SummaryTemplates
{
    private static readonly (string Id, string Name)[] Builtins =
    [
        ("general", "General meeting"), ("client-file-note", "Client file note"),
        ("lender-call", "Lender call"), ("stand-up", "Stand-up")
    ];

    public static IReadOnlyList<SummaryTemplate> Load(string? configDirectory = null)
    {
        var result = Builtins.Select(entry => LoadOne(entry.Id, entry.Name, configDirectory)).ToList();
        if (configDirectory is null) return result;
        var directory = Path.Combine(configDirectory, "meetings", "summary-types");
        if (!Directory.Exists(directory)) return result;
        foreach (var file in Directory.EnumerateFiles(directory, "*.md").Order(StringComparer.Ordinal))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!ValidId(id) || Builtins.Any(b => b.Id == id)) continue;
                var path = CustomPath(configDirectory, id);
                result.Add(Parse(id, File.ReadAllText(path)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                // A malformed or unsafe custom file cannot hide the other types.
            }
        }
        return result;
    }

    public static bool ValidId(string id) => id.Length is > 0 and <= 100 &&
        id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static string CustomPath(string configDirectory, string id)
    {
        if (!ValidId(id) || Builtins.Any(b => b.Id == id)) throw new ArgumentException("Invalid custom type id.");
        var directory = Path.Combine(Path.GetFullPath(configDirectory), "meetings", "summary-types");
        var path = Path.Combine(directory, id + ".md");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!RealPath(path).StartsWith(Path.GetFullPath(configDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) ||
            !string.Equals(RealPath(path), path, comparison)) throw new IOException("Summary type links are not permitted.");
        return path;
    }

    public static SummaryTemplate Parse(string id, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        if (lines[0] != "---" || end < 3 || !lines[1].StartsWith("name: ", StringComparison.Ordinal) ||
            lines[2] != "instructions: |" || lines.Skip(3).Take(end - 3).Any(l => !l.StartsWith("  ", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid summary type front block.");
        var name = lines[1][6..].Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Missing summary type name.");
        return new(id, name, string.Join("\n", lines.Skip(end + 1)))
        {
            Instructions = string.Join("\n", lines.Skip(3).Take(end - 3).Select(l => l[2..])), IsBuiltin = false,
        };
    }

    public static void Save(string configDirectory, SummaryTemplate template)
    {
        if (template.IsBuiltin || string.IsNullOrWhiteSpace(template.Name) || template.Name.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Invalid summary type.");
        var path = CustomPath(configDirectory, template.Id);
        var text = "---\nname: " + template.Name.Trim() + "\ninstructions: |\n" +
            string.Join("\n", template.Instructions.Replace("\r\n", "\n").Split('\n').Select(l => "  " + l)) + "\n---\n" + template.Markdown;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Dictum.Meetings.Store.AtomicFile.Write(path, stream => { using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(text); });
    }

    public static void Delete(string configDirectory, string id) => File.Delete(CustomPath(configDirectory, id));

    public static SummaryTemplate Get(string id, string? configDirectory = null)
    {
        return Load(configDirectory).FirstOrDefault(entry => entry.Id == id)
            ?? throw new ArgumentException("Unknown meeting template.", nameof(id));
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
            if (!entry.Exists) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            var target = entry.ResolveLinkTarget(true) ?? throw new IOException("Unresolved template link.");
            // Resolve target ancestors too, not just a chain on the final component.
            current = RealPath(target.FullName, depth + 1);
        }
        return current;
    }
}
