using System.Text;

namespace Dictum.Settings;

/// <summary>
/// "Create the key file…" on Settings > Clean-up (1.3): the config folder's .env (<see cref="AppPaths.EnvFile"/>, the one Dictum
/// reads first after the environment) with a short header in .env.example's comment style and one line for the chosen service's
/// variable holding <see cref="SecretStore.Placeholder"/>, which counts as not set. It never writes a key, and never touches a
/// .env that is already there: that file is opened as it is. Replaces the Help answer's Notepad steps for making the file.
/// </summary>
public static class KeyFile
{
    /// <summary>APPROVED 22/10/2026 22:52 AEDT (proposed 22/10/2026): do not reword. 1.3: the header of a key file Dictum creates, in .env.example's comment style.</summary>
    public const string Header =
        "# Dictum keys, created by Settings > Clean-up. Replace your-key-here with your key and save this file: Dictum reads it again by itself.\n" +
        "# A real environment variable of the same name wins over this file. Keys are never written to settings.json or the log.\n";

    /// <summary>The new file's text: the header, then "<paramref name="variable"/>=your-key-here".</summary>
    public static string Contents(string variable) => Header + "\n" + variable + "=" + SecretStore.Placeholder + "\n";

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
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Contents(variable));
            file.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(paths.EnvFile))
        {
            return false; // made by something else meanwhile: left alone
        }
    }
}
