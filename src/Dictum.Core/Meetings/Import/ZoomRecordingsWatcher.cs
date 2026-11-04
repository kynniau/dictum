namespace Dictum.Meetings.Import;

/// <summary>Recursively observes Zoom's dated subfolders. Callbacks run serially on the polling timer.</summary>
public sealed class ZoomRecordingsWatcher : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _stableFor;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly Dictionary<string, Observation> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ITimer? _timer;
    private string? _folder;
    private Action<string>? _onFinished;
    private bool _disposed;

    public bool ImportExisting { get; init; }

    public ZoomRecordingsWatcher(TimeProvider? timeProvider = null, TimeSpan? stabilityWindow = null,
        TimeSpan? pollInterval = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _stableFor = stabilityWindow ?? TimeSpan.FromSeconds(10);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_stableFor < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityWindow));
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public IDisposable Start(string folder, Action<string> onFinished)
    {
        ArgumentNullException.ThrowIfNull(onFinished);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_folder is not null) throw new InvalidOperationException("The watcher has already started.");
            var fullPath = Path.GetFullPath(folder);
            if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
            _folder = fullPath;
            _onFinished = onFinished;
            Scan(initial: true);
            _timer = _clock.CreateTimer(_ => Poll(), null, _pollInterval, _pollInterval);
            return this;
        }
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Scan(initial: false);
        }
    }

    private void Scan(bool initial)
    {
        // simplification: polling can miss files created and removed between ticks; retire if Zoom starts producing ephemeral recordings.
        try
        {
            foreach (var path in Directory.EnumerateFiles(_folder!, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (_disposed) return;
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)) continue;
                Observe(path, initial);
            }
        }
        catch (IOException) { /* The folder may disappear while Zoom moves a recording. Retry next tick. */ }
        catch (UnauthorizedAccessException) { /* Retry when folder access is restored. */ }
    }

    private void Observe(string path, bool initial)
    {
        try
        {
            var size = new FileInfo(path).Length;
            var now = _clock.GetTimestamp();
            if (!_files.TryGetValue(path, out var observed))
            {
                _files[path] = new(size, now, initial && !ImportExisting);
                return;
            }
            if (observed.Done) return;
            if (observed.Size != size)
            {
                _files[path] = new(size, now, false);
                return;
            }
            if (_clock.GetElapsedTime(observed.Since, now) < _stableFor) return;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (input.Length != size)
                {
                    _files[path] = new(input.Length, now, false);
                    return;
                }
            }
            _files[path] = observed with { Done = true };
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        _onFinished!(path);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _onFinished = null;
        }
    }

    private sealed record Observation(long Size, long Since, bool Done);
}
