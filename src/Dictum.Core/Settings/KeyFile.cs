using System.Text;

namespace Dictum.Settings;

/// <summary>
/// "Create the key file…" on Settings > Clean-up (1.3): the config folder's .env (<see cref="AppPaths.EnvFile"/>, the one Dictum
/// reads first after the environment) with a short header in .env.example's comment style and, since 04/11/2026, the full template
/// (<see cref="Contents"/>), every variable holding <see cref="SecretStore.Placeholder"/>, which counts as not set. It never writes a
/// key, and never touches a .env that is already there: that file is opened as it is. Replaces the Help answer's Notepad steps for
/// making the file.
/// </summary>
public static class KeyFile
{
    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. 1.3: the header of a key file Dictum creates, in .env.example's comment style.</summary>
    public const string Header =
        "# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself.\n" +
        "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log.\n";

    /// <summary>
    /// The new file's text (K-UI lane, 04/11/2026, brief §2 item 3): the approved <see cref="Header"/> first, byte for byte, a blank
    /// line, then the whole of the repository's .env.example (every variable and its comments, in its own words), each empty
    /// "VARIABLE=" line given <see cref="SecretStore.Placeholder"/>, which counts as not set. Still one line per variable (decision D4):
    /// several keys for one service live in the credential store only. .env.example is compiled in (Dictum.Core.csproj), so the file
    /// Dictum writes cannot drift from the template the repository carries.
    /// </summary>
    public static string Contents() => Header + "\n" + Template;

    /// <summary>The logical name Dictum.Core.csproj gives the embedded .env.example.</summary>
    internal const string TemplateResource = "Dictum.Settings.env.example";

    /// <summary>.env.example with LF line ends and the placeholder after every empty key line.</summary>
    internal static string Template { get; } = Placeholders(ReadTemplate());

    private static string ReadTemplate()
    {
        using var stream = typeof(KeyFile).Assembly.GetManifestResourceStream(TemplateResource)
                           ?? throw new InvalidOperationException($"{TemplateResource} is not compiled into Dictum.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Placeholders(string template) =>
        string.Join("\n", template.Split('\n').Select(line =>
            line.EndsWith('=') && SecretStore.KeyNames.Contains(line[..^1]) ? line + SecretStore.Placeholder : line));

    /// <summary>
    /// Creates the key file for <paramref name="variable"/> (one of <see cref="SecretStore.KeyNames"/>) unless one exists. True when
    /// it was created; false when a file was already there, which is left byte for byte as it was (the create cannot overwrite:
    /// it fails when the file exists, also when it appeared after a check).
    /// </summary>
    public static bool CreateIfMissing(AppPaths paths, string variable)
    {
        if (!SecretStore.KeyNames.Contains(variable)) throw new ArgumentException($"{variable} is not a key Dictum reads.", nameof(variable));
        if (File.Exists(paths.EnvFile)) return false;
        Directory.CreateDirectory(paths.ConfigDir);
        try
        {
            using var file = new FileStream(paths.EnvFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Contents());
            file.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(paths.EnvFile))
        {
            return false; // made by something else meanwhile: left alone
        }
    }
}
