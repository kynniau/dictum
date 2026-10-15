using System.Diagnostics;
using System.Text.Json;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>A catalogue model file on disk whose size or SHA-256 does not match: it is reported and not loaded.</summary>
public sealed class ModelIntegrityException(string message) : IOException(message);

/// <summary>
/// First-use check of a catalogue model file that Dictum did not download itself (copied by install.ps1
/// -ModelsFrom, fetched with curl, or left from an older build): before it is loaded the first time its size and
/// SHA-256 are compared with the catalogue. A match is remembered (path, size, last-write time) in
/// %LOCALAPPDATA%\Dictum\verified-models.json so later starts do not re-hash; downloads by Dictum are recorded
/// directly. Files outside the catalogue (chosen by path) have no reference hash and are loaded as they are; a chosen file
/// that shares a catalogue file's name (in any folder) is checked as that file, and a mismatch outside the models folder
/// is reported with "rename it", never "delete it".
/// </summary>
public sealed class ModelIntegrity(string cacheFile, ILog log, Func<string>? modelsDir = null)
{
    private static readonly object Gate = new();

    /// <summary>The catalogue's expectation for this file name (speech or cleanup), or null.</summary>
    public static DownloadSpec? Expected(string path) =>
        ModelCatalog.FindByFile(path)?.Download ?? CleanupCatalog.FindByFile(path)?.Download;

    /// <summary>
    /// Returns a short status for --selftest; throws <see cref="ModelIntegrityException"/> on a mismatch.
    /// </summary>
    // simplification: the "verified earlier" record trusts size + last-write time. Ceiling: a same-size file
    // replaced with its timestamp preserved is not re-hashed. Trigger to retire: a report of a swapped model file;
    // then re-hash on every load (measured 15/10/2026: 4.0 s for the 1.5 GB medium.en) or compare a partial hash.
    public string Verify(string path)
    {
        var spec = Expected(path);
        if (spec is null) return "not in the catalogue, so not hash-checked";
        var file = new FileInfo(path);
        if (spec.Bytes is long want && file.Length != want)
        {
            throw new ModelIntegrityException($"{file.Name} is {file.Length:N0} bytes but the catalogue says {want:N0}; it was not loaded. {Remedy(path)}");
        }
        if (spec.Sha256 is null) return "size matches; no published SHA-256 to check";
        var key = Path.GetFullPath(path);
        var stamp = Stamp(file, spec.Sha256);
        lock (Gate)
        {
            if (Read().TryGetValue(key, out var known) && known == stamp) return "SHA-256 verified earlier";
        }
        var sw = Stopwatch.StartNew();
        var actual = ModelDownloader.HashFileAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        if (!string.Equals(actual, spec.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            log.Error($"{file.Name}: SHA-256 {actual} does not match the catalogue ({spec.Sha256}); not loaded.");
            throw new ModelIntegrityException($"{file.Name} does not match the catalogue (SHA-256 {actual[..12]}…, expected {spec.Sha256[..12]}…); it was not loaded. {Remedy(path)}");
        }
        log.Info($"Verified {file.Name} against the catalogue on first use (SHA-256) in {sw.Elapsed.TotalSeconds:0.0} s.");
        Save(key, stamp);
        return "SHA-256 verified";
    }

    /// <summary>
    /// What to do about a mismatch. A file in the models folder is Dictum's to replace; a file chosen from anywhere else is
    /// the user's own (SPEC §17: a chosen path is only ever forgotten), checked only because it shares a catalogue file's
    /// name, so Dictum never tells the user to delete it. Without a models folder to compare with, the file is treated as
    /// the user's own.
    /// </summary>
    private string Remedy(string path) => modelsDir is not null && SameFolder(Path.GetDirectoryName(Path.GetFullPath(path)), modelsDir())
        ? "Delete it and download it again."
        : "It has the same name as a catalogue file, so it is checked as that file. Rename it, then choose it again.";

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Records a file Dictum has just downloaded and verified, so its first load does not hash it again.</summary>
    public void Remember(string path)
    {
        if (Expected(path)?.Sha256 is not { } sha || !File.Exists(path)) return;
        Save(Path.GetFullPath(path), Stamp(new FileInfo(path), sha));
    }

    /// <summary>Drops a deleted model's record (SPEC §17: deleting a model cleans its integrity record).</summary>
    public void Forget(string path) => Update(all => all.Remove(Path.GetFullPath(path)));

    /// <summary>Rewrites recorded paths under <paramref name="from"/> to <paramref name="to"/> (the one-time migration moved the folder).</summary>
    public void Rebase(string from, string to) => Update(all =>
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)) + Path.DirectorySeparatorChar;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to)) + Path.DirectorySeparatorChar;
        foreach (var key in all.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            all[target + key[prefix.Length..]] = all[key];
            all.Remove(key);
        }
        return true;
    });

    private static string Stamp(FileInfo file, string sha) => $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{sha.ToLowerInvariant()}";

    private Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(cacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cacheFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return []; // an unreadable record only costs a re-hash
        }
    }

    private void Save(string key, string stamp) => Update(all =>
    {
        all[key] = stamp;
        return true;
    });

    /// <summary>Reads the record, lets <paramref name="change"/> edit it, and writes it back if it says it changed.</summary>
    private void Update(Func<Dictionary<string, string>, bool> change)
    {
        lock (Gate)
        {
            try
            {
                var all = Read();
                if (!change(all) || (all.Count == 0 && !File.Exists(cacheFile))) return;
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                var temp = cacheFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, cacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not update the verified-model record {cacheFile} ({ex.GetType().Name}); a model may be hashed again next time.");
            }
        }
    }
}
