using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

/// <summary>
/// --download-model / --download-cleanup-model: headless model download for scripted deployments (SPEC §11).
/// Size and SHA-256 are verified against the catalogue. Refused when offline_only is on. Exit 0/1.
/// </summary>
public static class DownloadCommand
{
    /// <param name="http">Tests pass a client that never reaches a real host; production builds the download client.</param>
    public static async Task<int> RunAsync(CliOptions options, AppPaths paths, ILog log, HttpClient? http = null)
    {
        // The download needs no cleanup repair, so no GPU probe either.
        var config = ConfigLoader.Load(paths, new SecretRedactor(), Environment.GetEnvironmentVariable, _ => false, log.Warn);
        if (config.ForcedOffline)
        {
            // Its offline_only cannot be known, so nothing may be downloaded until the file is fixed.
            Console.Error.WriteLine($"error: {paths.SettingsFile} could not be read, so whether downloads are allowed (offline_only) is unknown. Fix the file, then run this again.");
            log.Warn("Download refused: settings.json could not be read.");
            return 1;
        }
        var settings = config.Settings;
        DownloadSpec spec;
        if (options.Mode == CliMode.DownloadCleanupModel)
        {
            spec = CleanupCatalog.Find(options.DownloadName!)!.Download;
        }
        else
        {
            var info = ModelCatalog.Find(options.DownloadName!);
            if (info is null) Console.Error.WriteLine($"warning: \"{options.DownloadName}\" is not in Dictum's model catalogue, so its size and SHA-256 cannot be verified.");
            spec = info?.Download ?? new DownloadSpec(ModelCatalog.DownloadUri(options.DownloadName!), ModelCatalog.FileName(options.DownloadName!), null, null);
        }
        var destination = Path.Combine(paths.ModelsDir(settings), spec.FileName);
        var integrity = new ModelIntegrity(paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(paths, settings));
        // The app's lookup (ModelFolders, the lens 17/10/2026): a copy in any model folder counts, the one that comes with Dictum
        // included, so "--download-model base.en" fetches no second copy of the bundled file.
        var present = spec.Bytes is null ? null : ModelFolders.Find(spec.FileName, spec.Bytes, paths, settings, integrity.Passes);
        return await DownloadAsync(spec, destination, settings.OfflineOnly, log, integrity, http, present);
    }

    /// <param name="integrity">Checks a copy already on this PC (size and SHA-256, or the record of an earlier check); null: none is looked for.</param>
    /// <param name="present">The copy the app's lookup found (<see cref="ModelFolders.Find"/>), checked instead of <paramref name="destination"/>.</param>
    internal static async Task<int> DownloadAsync(DownloadSpec spec, string destination, bool offlineOnly, ILog log, ModelIntegrity? integrity = null, HttpClient? http = null,
        string? present = null)
    {
        if (offlineOnly)
        {
            Console.Error.WriteLine($"error: {new OfflineOnlyException($"Download of {spec.FileName}").Message}");
            log.Warn($"Download of {spec.FileName} refused: offline_only is on.");
            return 1;
        }
        var existing = present ?? destination;
        if (integrity is not null && File.Exists(existing) && spec.Bytes is long bytes && new FileInfo(existing).Length == bytes)
        {
            string? status = null;
            try
            {
                status = integrity.Verify(existing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // ModelIntegrityException is an IOException
            {
            }
            if (status is not null)
            {
                // APPROVED 22/10/2026 copy (proposed 17/10/2026): the two lines below name the copy the app's lookup found, in
                // any model folder, and how it was checked ("SHA-256 verified", or "verified earlier"); they named the download folder's file and the hash.
                Console.Out.WriteLine(spec.Sha256 is null
                    ? $"{existing} is already present with the catalogue size ({bytes:N0} bytes; no published SHA-256 to check). Nothing to do."
                    : $"{existing} is already present and matches the catalogue ({bytes:N0} bytes, {status}). Nothing to do.");
                return 0;
            }
            Console.Error.WriteLine($"warning: {existing} has the right size but the wrong SHA-256; downloading it again.");
        }

        Console.Out.WriteLine($"Downloading {spec.Uri}");
        Console.Out.WriteLine($"        to {destination}");
        log.Info($"Download: {spec.Uri} -> {destination}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var redirected = Console.IsOutputRedirected;
        var lastDecile = -1;
        var progress = new SynchronousProgress<(long Done, long? Total)>(p =>
        {
            var pct = p.Total is long t && t > 0 ? (int)(100 * p.Done / t) : -1;
            var line = pct >= 0 ? $"  {pct,3}%  {p.Done / 1e6,8:0.0} / {p.Total / 1e6:0.0} MB" : $"  {p.Done / 1e6,8:0.0} MB";
            if (!redirected) Console.Out.Write("\r" + line);
            else if (pct / 10 != lastDecile)
            {
                lastDecile = pct / 10;
                Console.Out.WriteLine(line);
            }
        });

        try
        {
            using var owned = http is null ? ModelDownloader.CreateHttpClient(() => offlineOnly) : null;
            var attempts = await ModelDownloader.DownloadAsync(http ?? owned!, spec, destination, offlineOnly, progress, cts.Token, log);
            integrity?.Remember(destination);
            if (!redirected) Console.Out.WriteLine();
            var size = new FileInfo(destination).Length;
            var verified = spec.Sha256 is null ? "size checked" : "size and SHA-256 verified";
            Console.Out.WriteLine($"Done: {destination} ({size:N0} bytes, {verified}; {attempts} attempt{(attempts == 1 ? "" : "s")}).");
            log.Info($"Download finished: {destination} ({size} bytes, {verified}; {attempts} attempt(s)).");
            return 0;
        }
        catch (Exception ex)
        {
            if (!redirected) Console.Out.WriteLine();
            Console.Error.WriteLine(ex is DownloadFailedException ? $"error: {ex.Message}" : $"error: download failed: {ex.GetType().Name}: {ex.Message}");
            if (ex is DownloadFailedException { Resumable: true }) Console.Error.WriteLine("The partial file was kept: run the same command again to carry on from where it stopped.");
            log.Error($"Download failed: {ex.GetType().Name}: {ex.Message} {ModelDownloader.Describe(ex.InnerException)}".TrimEnd());
            return 1;
        }
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
