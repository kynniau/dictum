using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dictum.Meetings.Store;

public sealed partial class FileMeetingStore
{
    public void ExportBundle(MeetingId id, string destinationPath, string transferCode)
    {
        lock (Gate) WithKey(id, content =>
        {
            var destination = Path.GetFullPath(destinationPath);
            var first = Path.GetRelativePath(Root, destination).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if ((first.Length == 32 && first.All(char.IsAsciiHexDigitLower)) || first.StartsWith(".import-", StringComparison.Ordinal) ||
                destination.Equals(Path.Combine(Root, "index.json"), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(BundleText.PROPOSED_Invalid);
            var salt = RandomNumberGenerator.GetBytes(16);
            var transfer = TransferCode.Derive(transferCode, salt);
            try
            {
                var wrap = MeetingCrypto.Wrap(content, transfer, "transfer");
                var meta = JsonSerializer.SerializeToUtf8Bytes(Read(id) with { Wraps = [] });
                try
                {
                    AtomicFile.Write(destination, output =>
                    {
                        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                        var files = new List<BundleFile>();
                        foreach (var path in Directory.GetFiles(Folder(id), "*.enc").Order(StringComparer.Ordinal))
                        {
                            var name = Path.GetFileName(path);
                            if (!BundleFormat.Allowed(name) || name == "meta.json.enc") throw BundleFormat.Invalid();
                            using var input = File.OpenRead(path);
                            using var entry = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                            files.Add(new(name, input.Length));
                            input.CopyTo(entry);
                        }
                        using var sealedMeta = new MemoryStream();
                        using (var plain = new MemoryStream(meta)) MeetingCrypto.Encrypt(plain, sealedMeta, content, Identity(id, "meta.json.enc"));
                        files.Add(new("meta.json.enc", sealedMeta.Length));
                        sealedMeta.Position = 0;
                        using (var entry = archive.CreateEntry("meta.json.enc", CompressionLevel.NoCompression).Open()) sealedMeta.CopyTo(entry);
                        var manifest = new BundleManifest("dictum-bundle", 1, id.Value,
                            new("Dictum", typeof(FileMeetingStore).Assembly.GetName().Version!.ToString(), OperatingSystem.IsMacOS() ? "mac" : "windows"),
                            new("pbkdf2-hmac-sha256", 600000, salt, wrap.WrappedKey), files.ToArray());
                        using var json = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
                        JsonSerializer.Serialize(json, manifest, BundleFormat.Json);
                    });
                }
                finally { CryptographicOperations.ZeroMemory(meta); }
            }
            finally { CryptographicOperations.ZeroMemory(transfer); }
            return true;
        });
    }

    internal byte[] BundleOwnerKey() => _keyring.OwnerKey();
    internal void RefuseExistingBundle(MeetingId id)
    {
        lock (Gate)
            if (Directory.Exists(Folder(id)) || File.Exists(Folder(id))) throw new IOException(BundleText.PROPOSED_AlreadyHere);
    }
    internal void PublishBundle(string staged, MeetingMeta meta)
    {
        lock (Gate)
        {
            RefuseExistingBundle(meta.Id);
            JsonWrite(Path.Combine(staged, "meta.json"), meta);
            // meta.json carries a valid owner wrap: every removal of staging goes through DiscardStaging, which scrubs it first.
            Directory.Move(staged, Folder(meta.Id));
            try { RebuildIndex(); }
            catch { Directory.Move(Folder(meta.Id), staged); throw; }
        }
    }
    /// <summary>
    /// Removes a bundle staging folder (or a stray file at its path), zero-filling plaintext metadata and atomic-write
    /// temporaries first, as <see cref="Delete"/> does. Missing staging is not an error.
    /// </summary>
    internal void DiscardStaging(string staged) => DiscardStaging(staged, null);

    // Per-call test observer: invoked after durable scrubbing, immediately before folder deletion.
    // No global hook, and production callers cannot replace or bypass the scrub.
    internal void DiscardStaging(string staged, Action<string>? beforeDelete)
    {
        lock (Gate)
        {
            var full = Path.GetFullPath(staged);
            if (!Path.GetFileName(full).StartsWith(".import-", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(full), Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid bundle staging folder.");
            if (File.Exists(full)) { Scrub(full); File.Delete(full); return; }
            if (!Directory.Exists(full)) return;
            foreach (var plain in Directory.GetFiles(full, "meta.json*"))
                Scrub(plain);
            foreach (var temp in Directory.GetFiles(full, "*.tmp"))
                Scrub(temp);
            beforeDelete?.Invoke(full);
            Directory.Delete(full, true);
        }
    }
}
