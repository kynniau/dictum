using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.App;

/// <summary>
/// Why a download is stopped (decision of 19/10/2026): it sets what the user is told. The Cancel download link and a model
/// choice's "Stop … and download …" answer each say so in a balloon; quit and Remove Dictum completely only log it.
/// </summary>
public enum DownloadCancelReason { CancelLink, Choice, Quit, Remove }

/// <summary>Where a model file stands in the download queue: not in it, downloading now, or waiting its turn.</summary>
public enum DownloadState { None, Running, Waiting }

/// <summary>
/// The model downloads the user starts (SPEC §1, §8), one at a time: a download asked for while another runs waits its turn
/// (the first run's two models, a second calibration "Download and include", a model choice's "Download after it"), so the
/// running download always has the only cancellation source. Cancel stops the running download and drops the queue. Runs on
/// the UI thread; the downloader retries and resumes on its own (8 attempts) before this reports.
/// </summary>
internal sealed class ModelDownloads(HttpClient http, ILog log, Func<AppSettings> settings, ModelIntegrity? integrity, Action refreshTooltip,
    Action<string, bool> balloon, Func<DownloadSpec, string, bool>? refuseBeforeStart = null)
{
    private sealed record Job(DownloadSpec Spec, string Destination, Action OnSuccess, Action? OnFailure)
    {
        /// <summary>
        /// Run after <see cref="OnSuccess"/> when the file arrives, or after <see cref="OnFailure"/> when it does not: a model chosen
        /// again while it was on its way, or a preset that needs a file already on its way (<see cref="AlsoOnArrival"/>). Keyed, so
        /// a repeat of the same choice adds nothing.
        /// </summary>
        public List<(string Key, Action OnSuccess, Action? OnFailure)> Also { get; } = [];
    }

    private readonly Queue<Job> _queue = new();
    private CancellationTokenSource? _cts;
    private Job? _current;
    private string? _cancelBalloon; // what the running download's cancellation says (set by Cancel); null: the log only

    /// <summary>The running download's task (a cancelled download deletes its .part before it completes), or null.</summary>
    public Task? Running { get; private set; }

    /// <summary>True while a download runs or waits its turn (the tray then shows "Cancel download").</summary>
    public bool IsDownloading => _cts is not null || _queue.Count > 0;

    /// <summary>"downloading base.en 42%" while a download runs, else null.</summary>
    public string? Status { get; private set; }

    /// <summary>How far the running download has got (0-1), or null when none runs or its size is unknown.</summary>
    public double? Fraction { get; private set; }

    /// <summary>How far the running download has got, as the status line says it ("42%", or "12 MB" when its size is unknown), or null.</summary>
    public string? Progress { get; private set; }

    /// <summary>The running download's file name (ggml-base.en.bin), or null.</summary>
    public string? CurrentFile => _current?.Spec.FileName;

    /// <summary>The running download's destination, or null.</summary>
    public string? CurrentDestination => _current?.Destination;

    /// <summary>How many downloads have ended, arrived or not (counted before the status refresh that follows).</summary>
    public int Ended { get; private set; }

    /// <summary>A download to this file is running or waiting its turn.</summary>
    public bool IsFetching(string destination) => StateOf(destination) != DownloadState.None;

    /// <summary>Whether a download to this file is running, waiting its turn, or neither.</summary>
    public DownloadState StateOf(string destination) =>
        _current is { } running && Same(running.Destination, destination) ? DownloadState.Running
        : _queue.Any(job => Same(job.Destination, destination)) ? DownloadState.Waiting
        : DownloadState.None;

    /// <summary>Whether a download of this file name (to any folder) is running, waiting its turn, or neither: what a page listing the catalogue asks.</summary>
    public DownloadState StateOfFile(string fileName) =>
        _current is { } running && SameName(running, fileName) ? DownloadState.Running
        : _queue.Any(job => SameName(job, fileName)) ? DownloadState.Waiting
        : DownloadState.None;

    private static bool SameName(Job job, string fileName) => string.Equals(job.Spec.FileName, fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds <paramref name="onSuccess"/> to what runs when the download to <paramref name="destination"/> (running or waiting)
    /// arrives, after its own success action, and <paramref name="onFailure"/> to what runs when it fails, is cancelled or is
    /// dropped. A second call with the same <paramref name="key"/> for that download adds nothing (lens, 19/10/2026: repeat
    /// picks stacked one action each). False when no such download is in the queue.
    /// </summary>
    public bool AlsoOnArrival(string destination, string key, Action onSuccess, Action? onFailure = null)
    {
        var job = _current is { } running && Same(running.Destination, destination) ? running : _queue.FirstOrDefault(j => Same(j.Destination, destination));
        if (job is null) return false;
        if (job.Also.All(a => a.Key != key)) job.Also.Add((key, onSuccess, onFailure));
        return true;
    }

    /// <summary>How many actions wait on the download to <paramref name="destination"/> besides its own (tests count them).</summary>
    internal int AlsoCount(string destination) =>
        (_current is { } running && Same(running.Destination, destination) ? running : _queue.FirstOrDefault(j => Same(j.Destination, destination)))?.Also.Count ?? 0;

    /// <summary>
    /// The bytes the downloads running and waiting will still write to the drive holding <paramref name="folder"/>: each one's
    /// size less the partial file it already has there (Decision 15: a new download must fit beside them). Unknown sizes count 0.
    /// </summary>
    public long PendingBytes(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        long pending = 0;
        foreach (var job in (_current is { } running ? [running] : Array.Empty<Job>()).Concat(_queue))
        {
            if (job.Spec.Bytes is not long total || !string.Equals(Path.GetPathRoot(Path.GetFullPath(job.Destination)), root, StringComparison.OrdinalIgnoreCase)) continue;
            var part = new FileInfo(job.Destination + ".part");
            pending += Math.Max(0, total - (part.Exists ? part.Length : 0));
        }
        return pending;
    }

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

    /// <summary>
    /// Stops the running download (its .part is deleted) and drops the queue. <paramref name="reason"/> sets the balloon:
    /// "Download of X cancelled." for the Cancel download link, "Download of X stopped: you chose Y." for a model choice's
    /// Stop answer (<paramref name="chosen"/> is Y), none for quit and Remove Dictum completely (the log says it either way).
    /// </summary>
    public void Cancel(DownloadCancelReason reason, string? chosen = null)
    {
        var dropped = _queue.ToArray();
        _queue.Clear();
        if (_cts is { } cts)
        {
            log.Info(reason == DownloadCancelReason.Choice ? $"Download cancelled by the user: {chosen} was chosen instead." : "Download cancelled by the user.");
            var file = _current?.Spec.FileName;
            _cancelBalloon = reason switch
            {
                DownloadCancelReason.CancelLink => $"Download of {file} cancelled.",
                DownloadCancelReason.Choice => $"Download of {file} stopped: you chose {chosen}.", // PROPOSED copy, 19/10/2026
                _ => null,
            };
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
        _cancelBalloon = null;
        Progress = job.Spec.Bytes is > 0 ? "0%" : "0 MB";
        _cts = new CancellationTokenSource();
        Running = RunAsync(job, _cts);
    }

    private async Task RunAsync(Job job, CancellationTokenSource cts)
    {
        var succeeded = await DownloadAsync(job.Spec, job.Destination, cts);
        Report(job, succeeded); // a chained download (a preset's second file) starts here, ahead of the queue
        while (_cts is null && _queue.TryDequeue(out var next))
        {
            // Decision 15 checks the space again before a download starts: a queued one is checked when its turn comes.
            if (refuseBeforeStart?.Invoke(next.Spec, next.Destination) != true)
            {
                Run(next);
                break;
            }
            log.Info($"Queued download of {next.Spec.FileName} not started: not enough space on its drive.");
            Report(next, succeeded: false);
        }
    }

    private void Report(Job job, bool succeeded)
    {
        try
        {
            if (succeeded)
            {
                job.OnSuccess();
                foreach (var also in job.Also) also.OnSuccess();
            }
            else
            {
                job.OnFailure?.Invoke();
                foreach (var also in job.Also) also.OnFailure?.Invoke();
            }
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
            Progress = p.Total is long t && t > 0 ? $"{100 * p.Done / t}%" : $"{p.Done / 1_000_000} MB";
            Status = $"downloading {label} {Progress}";
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
            if (_cancelBalloon is { } said) balloon(said, false); // quit and Remove Dictum completely only log it
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
            Progress = null;
            _cancelBalloon = null;
            _cts = null;
            _current = null;
            cts.Dispose();
            Ended++;
            refreshTooltip();
        }
        return false;
    }
}
