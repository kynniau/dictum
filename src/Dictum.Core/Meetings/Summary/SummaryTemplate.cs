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
            var path = Path.Combine(configDirectory, "meeting-templates", id + ".md");
            if (File.Exists(path)) return new(id, name, File.ReadAllText(path));
        }
        using var resource = typeof(SummaryTemplates).Assembly.GetManifestResourceStream(
            $"Dictum.Meetings.Summary.Templates.{id}.md")
            ?? throw new InvalidOperationException("Missing built-in meeting template.");
        using var reader = new StreamReader(resource);
        return new(id, name, reader.ReadToEnd());
    }
}
