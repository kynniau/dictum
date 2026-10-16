using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.App;

/// <summary>
/// The model downloads the user starts (SPEC §1, §8), one at a time: a download asked for while another runs waits its turn
/// (the first run's two models, a second calibration "Download and include"), so the running download always has the only
/// cancellation source. Cancel stops the running download and drops the queue. Runs on the UI thread; the downloader
/// retries and resumes on its own (8 attempts) before this reports.
/// </summary>
internal sealed class ModelDownloads(HttpClient http, ILog log, Func<AppSettings> settings, ModelIntegrity? integrity, Action refreshTooltip,
    Action<string, bool> balloon)
{
    private sealed record Job(DownloadSpec Spec, string Destination, Action OnSuccess, Action? OnFailure);

    private readonly Queue<Job> _queue = new();
    private CancellationTokenSource? _cts;
    private Job? _current;

    /// <summary>The running download's task (a cancelled download deletes its .part before it completes), or null.</summary>
    public Task? Running { get; private set; }

    /// <summary>True while a download runs or waits its turn (the tray then shows "Cancel download").</summary>
    public bool IsDownloading => _cts is not null || _queue.Count > 0;

    /// <summary>"downloading base.en 42%" while a download runs, else null.</summary>
    public string? Status { get; private set; }

    /// <summary>How far the running download has got (0-1), or null when none runs or its size is unknown.</summary>
    public double? Fraction { get; private set; }

    /// <summary>How many downloads have ended, arrived or not (counted before the status refresh that follows).</summary>
    public int Ended { get; private set; }

    /// <summary>A download to this file is running or waiting its turn.</summary>
    public bool IsFetching(string destination) =>
        _current is { } running && Same(running.Destination, destination) || _queue.Any(job => Same(job.Destination, destination));

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <param name="onSuccess">Runs after the file arrived and was verified; an exception it throws is shown in a balloon.</param>
    /// <param name="onFailure">Runs when the download fails, is cancelled or is dropped from the queue.</param>
    public void Start(DownloadSpec spec, string destination, Action onSuccess, Action? onFailure = null)
    {
        var job = new Job(spec, destination, onSuccess, onFailure);
        if (_cts is null)
        {
            Run(job);
            return;
        }
        _queue.Enqueue(job);
        log.Info($"Download of {spec.FileName} queued: it starts when the download in progress ends.");
    }

    /// <summary>Tray "Cancel download" and quit: stops the running download (its .part is deleted) and drops the queue.</summary>
    public void Cancel()
    {
        var dropped = _queue.ToArray();
        _queue.Clear();
        if (_cts is { } cts)
        {
            log.Info("Download cancelled by the user.");
            cts.Cancel();
        }
        foreach (var job in dropped)
        {
            log.Info($"Queued download of {job.Spec.FileName} dropped.");
            Report(job, succeeded: false);
        }
    }

    private void Run(Job job)
    {
        _current = job;
        _cts = new CancellationTokenSource();
        Running = RunAsync(job, _cts);
    }

    private async Task RunAsync(Job job, CancellationTokenSource cts)
    {
        var succeeded = await DownloadAsync(job.Spec, job.Destination, cts);
        Report(job, succeeded); // a chained download (a preset's second file) starts here, ahead of the queue
        if (_cts is null && _queue.TryDequeue(out var next)) Run(next);
    }

    private void Report(Job job, bool succeeded)
    {
        try
        {
            if (succeeded) job.OnSuccess();
            else job.OnFailure?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error($"Using the downloaded {job.Spec.FileName} failed: {ex}");
            balloon($"{job.Spec.FileName} was downloaded and verified, but using it failed: {ex.Message}", true);
        }
    }

    private async Task<bool> DownloadAsync(DownloadSpec spec, string destination, CancellationTokenSource cts)
    {
        var label = Path.GetFileNameWithoutExtension(spec.FileName);
        log.Info($"User-requested download of {spec.Uri} to {destination}.");
        var progress = new Progress<(long Done, long? Total)>(p =>
        {
            if (cts.IsCancellationRequested) return;
            Status = p.Total is long t && t > 0 ? $"downloading {label} {100 * p.Done / t}%" : $"downloading {label} {p.Done / 1_000_000} MB";
            Fraction = p.Total is long total && total > 0 ? p.Done / (double)total : null;
            refreshTooltip();
        });
        try
        {
            var attempts = await ModelDownloader.DownloadAsync(http, spec, destination, () => settings().OfflineOnly, progress, cts.Token, log);
            integrity?.Remember(destination);
            log.Info($"Download complete and verified: {destination} ({attempts} attempt{(attempts == 1 ? "" : "s")}).");
            balloon($"Downloaded and verified {spec.FileName}.", false);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            log.Info($"Download of {spec.FileName} cancelled; the partial file was deleted.");
            balloon($"Download of {spec.FileName} cancelled.", false);
        }
        catch (DownloadFailedException ex) when (ex.Attempts > 1)
        {
            log.Error($"Model download failed: {ex.Message} {ModelDownloader.Describe(ex.InnerException)}".TrimEnd());
            balloon($"Model download failed after {ex.Attempts} attempts ({ex.Reason}). Check your network, then choose it again in Settings > Models"
                + (ex.Resumable ? ": it carries on from where it stopped." : "."), true);
        }
        catch (Exception ex)
        {
            log.Error($"Model download failed: {ex.GetType().Name}: {ex.Message}");
            balloon($"Download of {spec.FileName} failed: {ex.Message}", true);
        }
        finally
        {
            Status = null;
            Fraction = null;
            _cts = null;
            _current = null;
            cts.Dispose();
            Ended++;
            refreshTooltip();
        }
        return false;
    }
}
