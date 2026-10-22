using Dictum.Settings;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. 1.3: "Create the key file…" found a .env already there, left it as it was and opened it.</summary>
    public const string KeyFileExists = "Your key file already exists; it has been opened.";

    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. 1.3: "Create the key file…" made the file with the placeholder line and opened it.</summary>
    public static string KeyFileCreated(string variable) =>
        $"Your key file has been created and opened: put your key in place of {SecretStore.Placeholder} after {variable}=, then save it.";

    /// <summary>
    /// Settings > Clean-up → "Create the key file…": the config folder's .env with a placeholder line for <paramref name="variable"/>
    /// (<see cref="KeyFile.CreateIfMissing"/>; an existing file is never changed), then opened in the system's editor for .env files,
    /// as "Open the settings folder" opens the folder (<see cref="OpenPath"/>, at the real path in a packaged build). Returns the
    /// page's status line: what was done, or why the file could not be opened.
    /// </summary>
    public string CreateKeyFile(string variable)
    {
        var created = KeyFile.CreateIfMissing(Paths, variable);
        // APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword: both log lines below.
        _log.Info(created ? $"Key file created with a placeholder line for {variable} (no key written)." : "Key file already exists: opened as it is, not changed.");
        return OpenPath(RealPath(Paths.EnvFile), folder: false) ?? (created ? KeyFileCreated(variable) : KeyFileExists);
    }
}
