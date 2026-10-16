using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>Raised instead of any network call when offline_only is on.</summary>
public sealed class OfflineOnlyException(string what)
    : InvalidOperationException($"{what} refused: offline_only is on, so Dictum makes no network calls. Copy the file into the models folder instead (install.ps1 -ModelsFrom).");

/// <summary>The download did not complete; <see cref="Reason"/> is short enough for a balloon ("connection dropped").</summary>
public sealed class DownloadFailedException(string fileName, int attempts, string reason, Exception? inner = null, bool resumable = false)
    : IOException(attempts == 0
        ? $"{fileName} {reason}."
        : $"{fileName}: download failed after {attempts} attempt{(attempts == 1 ? "" : "s")} ({reason}).", inner)
{
    public int Attempts { get; } = attempts;
    public string Reason { get; } = reason;

    /// <summary>The retries ran out on a dropped connection: the .part file was kept, so the next attempt resumes.</summary>
    public bool Resumable { get; } = resumable;
}

/// <summary>How hard to try. <see cref="HttpVersion"/> null keeps .NET's default request version (HTTP/1.1).</summary>
public sealed record DownloadPolicy(int MaxAttempts, Func<int, TimeSpan> Backoff, TimeSpan StallTimeout, Version? HttpVersion = null)
{
    /// <summary>8 attempts, waiting 1, 2, 4, 8, 16, 16, 16 s between them; 30 s without a byte counts as a drop.</summary>
    public static DownloadPolicy Default { get; } = new(8, attempt => TimeSpan.FromSeconds(Math.Min(16, 1 << (attempt - 1))), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Downloads a model file: the one implementation behind the tray, the first-run picker, --download-*-model and
/// install.ps1 -Model. Only ever runs after the user asked for it and never when offline_only is on. Writes to a
/// .part file, hashes while writing, resumes a dropped connection with an HTTP Range request (keeping the .part),
/// verifies the exact size and SHA-256 from the catalogue, and only then renames. A failure deletes the .part, except
/// when the retries ran out on a dropped connection: then it is kept and the next request resumes from it (its bytes
/// are hashed first, so the final SHA-256 check still covers the whole file).
/// </summary>
public static partial class ModelDownloader
{
    public const string LoginRequired = "requires a Hugging Face login to download; use install.ps1 -ModelsFrom or download-model.ps1 with a token";

    /// <summary>
    /// TLS for model downloads. Measured 15/10/2026 on this network, ggml-large-v3-turbo.bin (1.62 GB) six times each:
    /// Windows' default (TLS 1.3 negotiated with huggingface.co and its CDN) had 20 dropped connections ("The
    /// decryption operation failed"), TLS 1.2 had 4. Both completed every time thanks to the resume, so TLS 1.2 is a
    /// reliability choice, not a security fix; the resume stays the safety net. DECISIONS.md has the table.
    /// </summary>
    public const SslProtocols DownloadTls = SslProtocols.Tls12;

    /// <summary>
    /// The HttpClient for model downloads and Check for updates (<see cref="UpdateCheck"/>), behind the offline_only guard.
    /// <paramref name="protocols"/> None lets Windows choose the TLS version. <paramref name="inner"/> replaces the socket
    /// handler under the guard: tests pass a fake server, so the guard they exercise is the real one.
    /// </summary>
    public static HttpClient CreateHttpClient(Func<bool> offlineOnly, SslProtocols protocols = DownloadTls, HttpMessageHandler? inner = null) =>
        new(new OfflineGuardHandler(offlineOnly, inner ?? CreateDownloadHandler(protocols)))
        {
            Timeout = Timeout.InfiniteTimeSpan, // downloads are bounded per read by DownloadPolicy.StallTimeout, the update check by UpdateCheck.CheckTimeout
        };

    /// <summary>The socket handler under the download client (separate so a test can pin its TLS choice).</summary>
    internal static SocketsHttpHandler CreateDownloadHandler(SslProtocols protocols = DownloadTls) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.None,
        SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = protocols },
        Proxy = LoopbackDirectProxy.Default,
    };

    /// <summary>Returns the number of attempts used.</summary>
    public static Task<int> DownloadAsync(HttpClient http, DownloadSpec spec, string destination, bool offlineOnly,
        IProgress<(long Done, long? Total)> progress, CancellationToken cancellationToken, ILog? log = null, DownloadPolicy? policy = null) =>
        DownloadAsync(http, spec, destination, () => offlineOnly, progress, cancellationToken, log, policy);

    /// <summary>Returns the number of attempts used. <paramref name="offlineOnly"/> is read again for every chunk, so turning
    /// offline_only on stops a download in flight.</summary>
    public static async Task<int> DownloadAsync(HttpClient http, DownloadSpec spec, string destination, Func<bool> offlineOnly,
        IProgress<(long Done, long? Total)> progress, CancellationToken cancellationToken, ILog? log = null, DownloadPolicy? policy = null)
    {
        if (offlineOnly()) throw new OfflineOnlyException($"Download of {spec.FileName}");
        if (spec.RequiresLogin) throw new DownloadFailedException(spec.FileName, 0, LoginRequired);
        log ??= NullLog.Instance;
        policy ??= DownloadPolicy.Default;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        try
        {
            long done = 0;
            var attempt = 1;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var mode = FileMode.Create;
            if (spec.Bytes is long want && File.Exists(part) && new FileInfo(part).Length is var kept && kept > 0 && kept < want)
            {
                await HashExistingAsync(part, sha, cancellationToken).ConfigureAwait(false);
                (done, mode) = (kept, FileMode.Open);
                log.Info($"Download of {spec.FileName}: resuming the partial file kept from an earlier attempt, at byte {kept:N0}.");
            }
            await using (var target = new FileStream(part, mode, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                target.Seek(done, SeekOrigin.Begin);
                for (; ; attempt++)
                {
                    try
                    {
                        done = await CopyAsync(http, spec, target, sha, done, offlineOnly, progress, policy, log, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (PermanentException ex)
                    {
                        throw new DownloadFailedException(spec.FileName, attempt, ex.Message);
                    }
                    catch (TransientException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        done = target.Position; // bytes are written before they are hashed, so the two always agree
                        log.Warn($"Download of {spec.FileName}: attempt {attempt}/{policy.MaxAttempts} failed at {done:N0} bytes: {ex.Reason}{(ex.InnerException is null ? "" : ": " + Describe(ex.InnerException))}");
                        if (attempt >= policy.MaxAttempts) throw new DownloadFailedException(spec.FileName, attempt, ex.Reason, ex.InnerException, resumable: done > 0);
                        var wait = policy.Backoff(attempt);
                        log.Info($"Download of {spec.FileName}: resuming from byte {done:N0} in {wait.TotalSeconds:0.#} s.");
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            var problem = Verify(spec, done, Convert.ToHexStringLower(sha.GetHashAndReset()));
            if (problem is not null) throw new InvalidDataException(problem);
            File.Move(part, destination, overwrite: true);
            return attempt;
        }
        catch (DownloadFailedException ex) when (ex.Resumable)
        {
            log.Info($"Download of {spec.FileName}: kept the partial file {part} so the next attempt resumes.");
            throw;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static async Task HashExistingAsync(string path, IncrementalHash sha, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) sha.AppendData(buffer, 0, read);
    }

    /// <summary>One attempt: streams from <paramref name="offset"/> (a Range request when resuming) into the .part file.</summary>
    private static async Task<long> CopyAsync(HttpClient http, DownloadSpec spec, FileStream target, IncrementalHash sha, long offset, Func<bool> offlineOnly,
        IProgress<(long Done, long? Total)> progress, DownloadPolicy policy, ILog log, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, spec.Uri);
        if (policy.HttpVersion is { } version)
        {
            request.Version = version;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(policy.StallTimeout);
        using var response = await Network(() => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token), policy, cancellationToken)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403) throw new PermanentException($"HTTP {status}: the file {LoginRequired}");
        if (status >= 500 || status is 408 or 429) throw new TransientException($"server error HTTP {status}", null);
        if (!response.IsSuccessStatusCode) throw new PermanentException($"HTTP {status}");

        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            log.Warn($"Download of {spec.FileName}: the server ignored the resume request (HTTP {status}); starting again from byte 0.");
            target.SetLength(0);
            sha.GetHashAndReset(); // discards what was hashed so far
            offset = 0;
        }
        else if (offset > 0 && response.Content.Headers.ContentRange?.From != offset)
        {
            target.SetLength(0); // unusable bytes: start the file again
            sha.GetHashAndReset();
            throw new TransientException($"the server resumed from byte {response.Content.Headers.ContentRange?.From} instead of {offset}; starting again from byte 0", null);
        }
        log.Info($"Download of {spec.FileName}: HTTP/{response.Version} {status}, from byte {offset:N0}.");

        // A file replaced upstream is refused before its bytes are fetched: every retry would otherwise download all of it,
        // only for Verify to reject it at the end.
        var announced = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is long length ? length + offset : null);
        if (spec.Bytes is long want && announced is long offered && offered != want)
        {
            throw new PermanentException($"the server offers {offered:N0} bytes but the catalogue says {want:N0}; the file on the server is not the one Dictum expects");
        }

        var total = spec.Bytes ?? (response.Content.Headers.ContentLength + offset);
        await using var source = await Network(() => response.Content.ReadAsStreamAsync(stall.Token), policy, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[1 << 20];
        var done = offset;
        var lastReport = offset;
        progress.Report((done, total));
        while (true)
        {
            if (offlineOnly()) throw new OfflineOnlyException($"Download of {spec.FileName}"); // the setting was turned on mid-download
            stall.CancelAfter(policy.StallTimeout); // restarts the stall timer for every read
            var read = await Network(() => source.ReadAsync(buffer, stall.Token).AsTask(), policy, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sha.AppendData(buffer, 0, read);
            done += read;
            if (done - lastReport >= 4 << 20)
            {
                progress.Report((done, total));
                lastReport = done;
            }
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress.Report((done, total));
        return done;
    }

    /// <summary>Runs one network step, turning drops and stalls into <see cref="TransientException"/>; the user's cancel passes through.</summary>
    private static async Task<T> Network<T>(Func<Task<T>> step, DownloadPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientException($"connection stalled: no data for {policy.StallTimeout.TotalSeconds:0.#} s", ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            throw new TransientException("connection dropped", ex);
        }
    }

    private sealed class PermanentException(string reason) : Exception(reason);

    private sealed class TransientException(string reason, Exception? inner) : Exception(reason, inner)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>The exception chain as "Type: message (HResult 0x…)" joined with " &lt;- ", with URL query strings removed.</summary>
    public static string Describe(Exception? ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 6; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {QueryString().Replace(e.Message, "$1?[query removed]")} (HResult 0x{e.HResult:X8})");
        }
        return string.Join(" <- ", parts);
    }

    [GeneratedRegex(@"(https?://[^\s?'""]+)\?[^\s'""]*")]
    private static partial Regex QueryString();

    /// <summary>Null when the file matches the catalogue (size and SHA-256, where known), else the reason.</summary>
    public static string? Verify(DownloadSpec spec, long bytes, string sha256)
    {
        if (spec.Bytes is long want && bytes != want)
            return $"{spec.FileName}: downloaded {bytes:N0} bytes but the catalogue says {want:N0}; the partial file was deleted.";
        if (spec.Sha256 is { } expected && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
            return $"{spec.FileName}: SHA-256 {sha256} does not match the catalogue ({expected}); the file was deleted.";
        return null;
    }

    /// <summary>SHA-256 of a file on disk.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort: a locked .part file is overwritten by the next attempt
        }
    }
}
