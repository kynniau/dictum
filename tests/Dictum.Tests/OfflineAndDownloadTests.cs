using System.Net;
using System.Security.Cryptography;
using Dictum.Cli;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>offline_only refusals and download integrity (SPEC §5), with no real network.</summary>
public sealed class OfflineAndDownloadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictate-dl-");

    /// <summary>
    /// Serves a byte array and honours Range requests (206 with Content-Range). Can break the stream of the first
    /// <see cref="BreakFirst"/> responses after <see cref="BreakAfter"/> bytes, stall the first <see cref="StallFirst"/>,
    /// ignore Range (answer 200 with the whole file), or answer queued status codes first.
    /// </summary>
    private sealed class FileServer(byte[] content, int breakAfter = -1) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<long?> RangeStarts { get; } = [];
        public int BreakFirst { get; init; } = breakAfter > 0 ? 1 : 0;
        public int BreakAfter { get; init; } = breakAfter > 0 ? breakAfter : 1 << 20;
        public int StallFirst { get; init; }
        public bool IgnoreRange { get; init; }
        public Queue<HttpStatusCode> Statuses { get; } = new();
        public int WrongRangeOnRequest { get; init; } = -1; // answers 206 but from byte 0
        public (int Chunk, int DelayMs) Slow { get; init; } // delivers Chunk bytes per read, DelayMs apart

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges.First().From;
            RangeStarts.Add(from);
            if (Statuses.TryDequeue(out var queued)) return Task.FromResult(new HttpResponseMessage(queued));
            var partial = from is not null && !IgnoreRange;
            var wrong = partial && Requests == WrongRangeOnRequest;
            var start = partial && !wrong ? (int)from!.Value : 0;
            var body = content[start..];
            Stream stream = Requests <= StallFirst ? new StallingStream()
                : Requests <= StallFirst + BreakFirst ? new BreakingStream(body, BreakAfter)
                : Slow.Chunk > 0 ? new SlowStream(body, Slow.Chunk, Slow.DelayMs)
                : new MemoryStream(body);
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = body.Length;
            if (partial) response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(start, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>A live but slow connection: a little data at a time, never pausing as long as the stall limit.</summary>
    private sealed class SlowStream(byte[] data, int chunk, int delayMs) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMs, cancellationToken);
            var n = Read(buffer.Span[..Math.Min(chunk, buffer.Length)]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>A connection that never delivers a byte.</summary>
    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>Delivers some bytes, then fails like a dropped TLS connection.</summary>
    private sealed class BreakingStream(byte[] data, int limit) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= limit) throw new IOException("The decryption operation failed (simulated).");
            return base.Read(buffer, offset, (int)Math.Min(count, limit - Position));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var n = Read(array, 0, array.Length);
            array.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
    }

    private static readonly IProgress<(long, long?)> NoProgress = new Progress<(long, long?)>();

    private (byte[] Content, DownloadSpec Spec) Sample(string? wrongHash = null)
    {
        var content = new byte[3 << 20];
        new Random(42).NextBytes(content);
        var hash = wrongHash ?? Convert.ToHexStringLower(SHA256.HashData(content));
        return (content, new DownloadSpec(new Uri("https://huggingface.co/test/resolve/main/x.gguf"), "x.gguf", content.Length, hash));
    }

    [Fact]
    public async Task Verified_download_is_renamed_into_place()
    {
        var (content, spec) = Sample();
        var target = Path.Combine(_dir.FullName, "x.gguf");
        await ModelDownloader.DownloadAsync(new HttpClient(new FileServer(content)), spec, target, false, NoProgress, CancellationToken.None);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Hash_mismatch_deletes_the_part_file_and_reports()
    {
        var (content, spec) = Sample(wrongHash: new string('0', 64));
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(new FileServer(content)), spec, target, false, NoProgress, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    private static readonly DownloadPolicy Fast = new(8, _ => TimeSpan.Zero, TimeSpan.FromSeconds(5));

    private sealed class ListProgress : IProgress<(long Done, long? Total)>
    {
        public List<long> Done { get; } = [];
        public void Report((long Done, long? Total) value) => Done.Add(value.Done);
    }

    [Fact]
    public async Task A_dropped_connection_is_resumed_with_a_range_request_and_still_verified()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content, breakAfter: 1 << 20);
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var progress = new ListProgress();
        var log = new RecordingLog();
        var attempts = await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, progress, CancellationToken.None, log, Fast);
        Assert.Equal(2, attempts);
        Assert.Equal([null, 1L << 20], server.RangeStarts); // the second request asked for the rest only
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target + ".part"));
        var resumed = progress.Done.SkipWhile(d => d < 1 << 20).ToList();
        Assert.Equal(1L << 20, resumed[0]); // progress continues from the resumed offset, never back to 0
        Assert.Equal(content.Length, resumed[^1]);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("attempt 1/8 failed at 1,048,576 bytes: connection dropped")
            && l.Message.Contains("IOException: The decryption operation failed (simulated). (HResult 0x"));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_restarts_from_zero_and_still_verifies()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content, breakAfter: 1 << 20) { IgnoreRange = true };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var attempts = await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, NullLog.Instance, Fast);
        Assert.Equal(2, attempts);
        Assert.Equal([null, 1L << 20], server.RangeStarts);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)); // hash restarted with the file
    }

    [Fact]
    public async Task Retries_stop_at_the_maximum_and_keep_the_part_file_for_the_next_attempt()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content) { BreakFirst = 100, BreakAfter = 512 << 10 };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var log = new RecordingLog();
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, log, Fast with { MaxAttempts = 2 }));
        Assert.Equal((2, "connection dropped", true), (ex.Attempts, ex.Reason, ex.Resumable));
        Assert.Equal("x.gguf: download failed after 2 attempts (connection dropped).", ex.Message);
        Assert.Equal([null, 512L << 10], server.RangeStarts);
        Assert.Equal(2, log.Lines.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("failed at")));
        Assert.Equal(1L << 20, new FileInfo(target + ".part").Length); // kept
        Assert.False(File.Exists(target));

        // The user chooses the model again: it resumes from the kept bytes and the hash covers the whole file.
        var healthy = new FileServer(content);
        Assert.Equal(1, await ModelDownloader.DownloadAsync(new HttpClient(healthy), spec, target, false, NoProgress, CancellationToken.None, log, Fast));
        Assert.Equal([1L << 20], healthy.RangeStarts);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_kept_part_file_with_the_wrong_bytes_fails_the_hash_and_is_deleted()
    {
        var (content, spec) = Sample();
        var target = Path.Combine(_dir.FullName, "x.gguf");
        await File.WriteAllBytesAsync(target + ".part", new byte[1 << 20], TestContext.Current.CancellationToken); // zeros, not the file
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(new FileServer(content)), spec, target, false, NoProgress, CancellationToken.None, NullLog.Instance, Fast));
        Assert.False(File.Exists(target + ".part"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_resume_that_starts_at_the_wrong_byte_is_rejected_and_restarted_from_zero()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content, breakAfter: 1 << 20) { WrongRangeOnRequest = 2 };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var log = new RecordingLog();
        Assert.Equal(3, await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, log, Fast));
        Assert.Equal([null, 1L << 20, null], server.RangeStarts);
        Assert.Contains("the server resumed from byte 0 instead of 1048576; starting again from byte 0", log.All);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_slow_but_live_connection_is_not_a_stall()
    {
        // The stall limit is a real timer (CancelAfter in ModelDownloader), so this test cannot be free of the clock; it is built so
        // that scheduling delay on a loaded runner cannot reach it. Seven reads (six of 512 KiB, then the end of the file) arrive
        // 250 ms apart: 1.75 s in all, longer than the 1.4 s limit, yet no gap is longer than 250 ms. Only restarting the timer on
        // every read lets it pass, and a read would have to be held up 1.15 s beyond its delay to be misreported as a stall.
        // (Before: 12 reads 40 ms apart against a 150 ms limit, so a 110 ms hiccup on a CI runner failed it.)
        var (content, spec) = Sample();
        var gap = TimeSpan.FromMilliseconds(250);
        var stallLimit = TimeSpan.FromMilliseconds(1400);
        var server = new FileServer(content) { Slow = (512 << 10, (int)gap.TotalMilliseconds) };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, NullLog.Instance,
            Fast with { StallTimeout = stallLimit }));
        Assert.True(sw.Elapsed > stallLimit, $"took {sw.Elapsed}"); // longer than the stall limit overall, by construction
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Turning_offline_only_on_stops_a_download_in_flight()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content) { Slow = (256 << 10, 10) };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var offline = false;
        var download = ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, () => offline, new FlipProgress(() => offline = true),
            CancellationToken.None, NullLog.Instance, Fast);
        await Assert.ThrowsAsync<OfflineOnlyException>(() => download);
        Assert.Equal(1, server.Requests);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    private sealed class FlipProgress(Action flip) : IProgress<(long Done, long? Total)>
    {
        public void Report((long Done, long? Total) value) => flip(); // the first report comes once the response has started
    }

    [Fact]
    public async Task Cancelling_during_the_backoff_stops_within_one_step()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content) { BreakFirst = 100, BreakAfter = 1 << 20 };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var log = new RecordingLog();
        using var cts = new CancellationTokenSource();
        var policy = DownloadPolicy.Default with { Backoff = _ => TimeSpan.FromSeconds(30) };
        var download = ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, cts.Token, log, policy);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!log.All.Contains("attempt 1/8 failed") && wait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Contains("resuming from byte", log.All); // now inside the 30 s backoff
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {sw.Elapsed}");
        Assert.Equal(1, server.Requests);
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_stalled_connection_counts_as_a_drop_and_is_retried()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content) { StallFirst = 1 };
        var target = Path.Combine(_dir.FullName, "x.gguf");
        var log = new RecordingLog();
        var attempts = await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, log,
            Fast with { StallTimeout = TimeSpan.FromMilliseconds(300) });
        Assert.Equal(2, attempts);
        Assert.Contains("connection stalled: no data for 0.3 s", log.All);
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_login_wall_is_reported_as_such_and_not_retried(HttpStatusCode status)
    {
        var (content, spec) = Sample();
        var server = new FileServer(content);
        server.Statuses.Enqueue(status);
        var ex = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(server), spec, Path.Combine(_dir.FullName, "x.gguf"), false, NoProgress, CancellationToken.None, NullLog.Instance, Fast));
        Assert.Contains(ModelDownloader.LoginRequired, ex.Message);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void A_login_wall_points_at_Settings_not_at_scripts_the_Store_package_lacks()
    {
        Assert.Contains("Settings > Models", ModelDownloader.LoginRequired);
        Assert.DoesNotContain(".ps1", ModelDownloader.LoginRequired);
    }

    [Fact]
    public void An_offline_refusal_points_at_Settings_not_at_install_ps1_which_the_Store_package_lacks()
    {
        var message = new OfflineOnlyException("Download of ggml-base.en.bin").Message;
        Assert.Contains("Copy the file into the models folder instead (Settings > Storage shows the folder).", message);
        Assert.DoesNotContain(".ps1", message);
    }

    [Fact]
    public async Task A_server_error_is_retried()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content);
        server.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        var target = Path.Combine(_dir.FullName, "x.gguf");
        Assert.Equal(2, await ModelDownloader.DownloadAsync(new HttpClient(server), spec, target, false, NoProgress, CancellationToken.None, NullLog.Instance, Fast));
        Assert.Equal(content, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_logged_exception_chain_keeps_types_and_drops_signed_query_strings()
    {
        var ex = new IOException("The decryption operation failed, see inner exception. https://cdn.example/x.bin?X-Amz-Signature=secret123",
            new System.Security.Authentication.AuthenticationException("inner detail"));
        var text = ModelDownloader.Describe(ex);
        Assert.DoesNotContain("secret123", text);
        Assert.Contains("https://cdn.example/x.bin?[query removed]", text);
        Assert.Contains("IOException: The decryption operation failed", text);
        Assert.Contains(" <- AuthenticationException: inner detail (HResult 0x", text);
    }

    [Fact]
    public void The_default_policy_is_eight_attempts_with_capped_doubling_backoff_and_a_30_second_stall_limit()
    {
        var p = DownloadPolicy.Default;
        Assert.Equal(8, p.MaxAttempts);
        Assert.Equal([1, 2, 4, 8, 16, 16, 16], Enumerable.Range(1, 7).Select(a => p.Backoff(a).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(30), p.StallTimeout);
        Assert.Null(p.HttpVersion);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, ModelDownloader.DownloadTls); // measured choice, see DECISIONS.md
        // And the client production builds really uses it: its socket handler, behind the offline guard (read through the
        // invoker's private field: HttpClient does not expose its handler).
        using var http = ModelDownloader.CreateHttpClient(() => false);
        var guard = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(http);
        var sockets = Assert.IsType<SocketsHttpHandler>(Assert.IsType<OfflineGuardHandler>(guard).InnerHandler);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, sockets.SslOptions.EnabledSslProtocols);
    }

    [Fact]
    public async Task Offline_only_refuses_a_download_before_any_network_call()
    {
        var (content, spec) = Sample();
        var server = new FileServer(content);
        await Assert.ThrowsAsync<OfflineOnlyException>(() =>
            ModelDownloader.DownloadAsync(new HttpClient(server), spec, Path.Combine(_dir.FullName, "x.gguf"), offlineOnly: true, NoProgress, CancellationToken.None));
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData("anthropic", null, true)]
    [InlineData("openai-compatible", "https://gateway.example.com/v1", true)]
    [InlineData("openai-compatible", "http://localhost:11434/v1", false)]
    [InlineData("openai-compatible", "http://127.0.0.1:1234/v1", false)]
    [InlineData("local", null, false)]
    [InlineData("none", null, false)]
    public void Offline_only_refuses_cloud_providers_only(string provider, string? baseUrl, bool refused)
    {
        var settings = new LlmSettings { Provider = provider, BaseUrl = baseUrl };
        Assert.Equal(refused, TranscriptCleaner.OfflineRefusal(settings, offlineOnly: true) is not null);
        Assert.Null(TranscriptCleaner.OfflineRefusal(settings, offlineOnly: false));
    }

    [Fact]
    public async Task Offline_only_turns_a_cloud_provider_into_the_rule_based_tidy_without_a_call()
    {
        var secrets = SecretStore.FromContents("ANTHROPIC_API_KEY=k-test-123456\n", null, _ => null);
        var server = new FileServer([]);
        var log = new RecordingLog();
        var cleaner = TranscriptCleaner.Create(new LlmSettings { Provider = "anthropic" }, offlineOnly: true, secrets, new HttpClient(server), log);
        Assert.False(cleaner.UsesModel);
        Assert.Contains("offline_only", cleaner.Status);
        var result = await cleaner.CleanAsync("um hello there", TestContext.Current.CancellationToken);
        Assert.Equal("Hello there.", result.Text);
        Assert.Equal(0, server.Requests);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("offline_only"));
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(true);
        }
        catch (IOException)
        {
        }
    }
}
