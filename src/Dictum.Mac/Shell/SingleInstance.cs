using System.Net.Sockets;
using Dictum.Platform;

namespace Dictum.Mac.Shell;

/// <summary>
/// The single-instance lock on macOS (SPEC §8), pure .NET: a lock file opened with FileShare.None, which .NET on Unix
/// turns into an advisory lock that a second open of the same file fails on, even inside one process (MacShellTests
/// proves it on this Mac), and which the system lets go when the process ends however it ends. The running copy also
/// listens on a Unix domain socket beside the lock; a later launch connects, sends one byte and reports
/// <see cref="InstanceOutcome.SignalledRunningCopy"/>. The outcomes and the bounded wait mirror the Windows build's mutex
/// and event (src/Dictum.Windows/Shell/SingleInstance.cs). The lock file is never deleted (deleting it would let two
/// copies lock two different files); the socket file is deleted when the listener stops.
/// </summary>
internal sealed class SingleInstance : IInstanceLock
{
    public const string LockFileName = "dictum.lock";
    public const string SocketFileName = "dictum.sock";

    /// <summary>The byte a later launch sends: "show yourself".</summary>
    internal const byte ShowYourself = 1;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private readonly FileStream? _lock;
    private readonly string _socketFile;

    private SingleInstance(FileStream? lockStream, string socketFile, InstanceOutcome outcome)
    {
        _lock = lockStream;
        _socketFile = socketFile;
        Outcome = outcome;
    }

    public InstanceOutcome Outcome { get; }

    /// <summary>
    /// Takes the lock, or signals the running copy through its socket. When the lock is held but nothing listens yet (the
    /// other copy is still starting, or quitting: it stops listening first), tries both every 250 ms for up to
    /// <paramref name="wait"/>: a copy that finishes quitting hands the lock over, one that finishes starting is signalled.
    /// </summary>
    public static SingleInstance Acquire(string lockFile, string socketFile, TimeSpan wait)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockFile))!);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (TryLock(lockFile) is { } stream) return new SingleInstance(stream, socketFile, InstanceOutcome.Owned);
            if (TrySignal(socketFile)) return new SingleInstance(null, socketFile, InstanceOutcome.SignalledRunningCopy);
            var left = wait - waited.Elapsed;
            if (left <= TimeSpan.Zero) return new SingleInstance(null, socketFile, InstanceOutcome.TimedOut);
            Thread.Sleep(left < Poll ? left : Poll);
        }
    }

    private static FileStream? TryLock(string lockFile)
    {
        try
        {
            return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null; // another process holds it
        }
    }

    /// <summary>Connects to the running copy's socket and sends one byte; false when nothing listens there.</summary>
    internal static bool TrySignal(string socketFile)
    {
        if (!File.Exists(socketFile)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketFile));
            socket.Send([ShowYourself]);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
        {
            return false; // a stale socket file (ECONNREFUSED), or a path longer than the system allows
        }
    }

    /// <summary>
    /// Listens on the socket until the returned handle is disposed, calling <paramref name="onSignal"/> on a thread-pool
    /// thread for every launch that signals. Only the copy that holds the lock listens, so any socket file
    /// already there is a stale one from a copy that ended without cleaning up, and is removed before binding.
    /// </summary>
    // simplification: a Unix socket path is limited to 104 bytes on macOS (sun_path); the socket lives beside settings in
    // ~/Library/Application Support/Dictum, about 50 bytes plus the home folder's length. With a home path over about 50
    // characters the bind fails, nothing listens, and a second launch waits out its 30 s and exits (TimedOut, logged).
    // Retire when a tester's home path is that long: bind in a shorter per-user folder (confstr _CS_DARWIN_USER_TEMP_DIR).
    public IDisposable ListenForOtherLaunches(Action onSignal)
    {
        if (Outcome != InstanceOutcome.Owned) throw new InvalidOperationException("Only the copy that holds the lock listens for other launches.");
        if (File.Exists(_socketFile)) File.Delete(_socketFile);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketFile));
            listener.Listen(4);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        var stop = new Listener(listener, _socketFile);
        _ = Task.Run(() => stop.RunAsync(onSignal));
        return stop;
    }

    /// <summary>
    /// The accept loop, asynchronous so that stopping it is a cancellation. The first version used a synchronous Accept on
    /// its own thread and MacShellTests hung in the listener test on this Mac (23/10/2026); with AcceptAsync and a
    /// cancellation token it passes. That disposing a socket does not wake a blocked Accept is the inferred cause, not traced.
    /// </summary>
    private sealed class Listener(Socket socket, string socketFile) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public async Task RunAsync(Action onSignal)
        {
            var buffer = new byte[1];
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await socket.AcceptAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return; // stopped
                }
                using (client)
                {
                    try
                    {
                        // A connection that never sends cannot stall the next launch's signal.
                        using var patience = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        patience.CancelAfter(TimeSpan.FromSeconds(1));
                        if (await client.ReceiveAsync(buffer, SocketFlags.None, patience.Token).ConfigureAwait(false) == 1 && buffer[0] == ShowYourself) onSignal();
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        // the other launch went away before its byte arrived
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            socket.Dispose();
            try
            {
                File.Delete(socketFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the next owner removes a stale file before binding
            }
        }
    }

    public void Dispose() => _lock?.Dispose();
}
