using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.App;
using Dictum.Settings;
using Dictum.Transcribe;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Check for updates (decision 11, 16/10/2026): only when the user presses the button, only in the direct download. One GET
/// of version.json carrying nothing about the user, compared on the numeric version; refused under offline_only before
/// anything is sent; never in the Store build. Every request here goes to a fake server: nothing reaches the network.
/// </summary>
public class UpdateCheckTests
{
    // APPROVED 22/10/2026 (proposed 16/10/2026); do not reword. Pinned word for word, independently of the
    // app's copy (UpdateCheck), so any change is deliberate.
    private const string ProposedButton = "Check for updates";
    private const string ProposedPrivacyLine = "Checking reads one small file from dictum.kynni.au and sends nothing about you.";
    private static string ProposedLatestLine(string version) => $"You have the latest version ({version}).";
    private static string ProposedNewerLine(string version) => $"{version} is available.";
    private const string ProposedDownloadPageButton = "Open the download page";
    private const string ProposedOfflineOnlyLine = "Offline only is on, so Dictum did not check.";
    private const string ProposedFailedLine = "Could not check right now. Try again later.";
    private const string ProposedStoreLine = "Updates come from the Microsoft Store.";

    // The site's sentence for dictum.kynni.au (the privacy policy and the landing page), and the privacy policy's short-version
    // bullet. APPROVED 22/10/2026 (proposed 16/10/2026); do not reword.
    private const string ProposedSiteSentence = "Only when you open the privacy policy or the full guide from Settings, or click Check for updates in the direct-download version, which reads one small file and sends nothing about you; the Store build is updated by the Store.";
    private const string ProposedShortVersionBullet = "<li><strong>Dictum goes online only when you ask it to:</strong> to download a model, to check for updates in the direct-download version, or, if you switch it on, to send text to a cloud clean-up service or a server you choose.</li>";

    private const string DownloadPage = "https://github.com/kynniau/dictum/releases/latest";

    /// <summary>version.json as dictum.kynni.au served it on 16/10/2026.</summary>
    private const string LiveFile = """{"version":"1.0.0","notes":"First public release.","published":"2026-10-16"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What one request carried when it reached the network (copied: the check disposes the request).</summary>
    private sealed record Seen(HttpMethod Method, Uri? Uri, string[] Headers, bool HasContent);

    /// <summary>Plays dictum.kynni.au: answers every request with <paramref name="status"/> and the content, keeping what each request carried.</summary>
    private sealed class FakeSite(HttpStatusCode status, Func<HttpContent> content) : HttpMessageHandler
    {
        public FakeSite(HttpStatusCode status, string body) : this(status, () => new StringContent(body, Encoding.UTF8, "application/json"))
        {
        }

        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(new Seen(request.Method, request.RequestUri, [.. request.Headers.Select(h => h.Key)], request.Content is not null));
            return Task.FromResult(new HttpResponseMessage(status) { Content = content(), RequestMessage = request });
        }
    }

    /// <summary>Takes the request and never answers: only the check giving up ends it.</summary>
    private sealed class SilentSite : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("never answers");
        }
    }

    /// <summary>
    /// Answers only once <see cref="Release"/> is called, so a check stays in flight. With <paramref name="failFirst"/>, the
    /// first request is answered at once with HTTP 500.
    /// </summary>
    private sealed class GateSite(string body, bool failFirst = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1 && failFirst) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request };
            await _gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }

    /// <summary>A body whose length is not known up front (no Content-Length), as a chunked answer arrives.</summary>
    private sealed class NoLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>
    /// A client built as the check's own is (<see cref="ModelDownloader.CreateHttpClient"/>, offline_only guard included), with
    /// the network replaced by <paramref name="site"/>.
    /// </summary>
    private static HttpClient Client(HttpMessageHandler site, Func<bool>? offlineOnly = null) => ModelDownloader.CreateHttpClient(offlineOnly ?? (() => false), inner: site);

    /// <summary>The check's own client exactly as production builds it (<see cref="AppRules.CreateClients"/>), real socket handler included.</summary>
    private static HttpClient UpdateClient()
    {
        var (providers, downloads, updates) = AppRules.CreateClients(() => false);
        providers.Dispose();
        downloads.Dispose();
        return updates;
    }

    /// <summary>
    /// The offline_only guard at the top of a client <see cref="AppRules.CreateClients"/> built. HttpClient does not expose its
    /// handler: read through the invoker's private field, as the TLS pin does.
    /// </summary>
    private static OfflineGuardHandler Guard(HttpClient http) =>
        Assert.IsType<OfflineGuardHandler>(typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(http));

    /// <summary>Between the guard and the real socket handler: sends the request for dictum.kynni.au to a listener on this PC instead.</summary>
    private sealed class ToListener(int port, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{port}{request.RequestUri!.AbsolutePath}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Between the guard and the real socket handler: counts the bytes the check reads from the answer. A server cannot say:
    /// the sockets buffer what it sends before anything is read.
    /// </summary>
    private sealed class CountsReads(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private long _read;

        public long Read => Interlocked.Read(ref _read);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Content = new StreamContent(new CountingStream(await response.Content.ReadAsStreamAsync(cancellationToken), n => Interlocked.Add(ref _read, n)));
            return response;
        }
    }

    /// <summary>A stream with no length (as a chunked answer arrives) that tells <paramref name="counted"/> how much each read returned.</summary>
    private sealed class CountingStream(Stream inner, Action<int> counted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await inner.ReadAsync(buffer, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int Counted(int read)
        {
            counted(read);
            return read;
        }
    }

    private static Task<UpdateCheckResult> Check(HttpClient http, string running = "1.0.0", bool packaged = false, bool offlineOnly = false, TimeSpan? timeout = null) =>
        UpdateCheck.CheckAsync(http, running, packaged, () => offlineOnly, NullLog.Instance, Ct, timeout);

    [Fact]
    public async Task One_check_sends_one_GET_to_the_exact_address_with_nothing_about_the_user()
    {
        var site = new FakeSite(HttpStatusCode.OK, LiveFile);
        using var http = Client(site);
        var result = await Check(http);
        var seen = Assert.Single(site.Requests);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("https://dictum.kynni.au/version.json", seen.Uri?.AbsoluteUri);
        Assert.Equal("", seen.Uri!.Query); // no query string: no version, no identifier
        Assert.Empty(seen.Headers); // no header of Dictum's own: no User-Agent, no cookie, no identifier
        Assert.False(seen.HasContent);
        Assert.Equal(UpdateStatus.Latest, result.Status);
    }

    [Fact]
    public async Task On_the_wire_the_check_s_own_client_adds_no_header_but_Host()
    {
        // The fake-server tests see the request as Dictum builds it; this reads what the real socket handler writes, from a
        // listener on this PC (a loopback address: nothing leaves it).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(Ct);
                var stream = client.GetStream();
                var received = new StringBuilder();
                var buffer = new byte[8192];
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, Ct);
                    if (read == 0) break;
                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {LiveFile.Length}\r\nConnection: close\r\n\r\n{LiveFile}"), Ct);
                return received.ToString();
            }, Ct);
            using var http = UpdateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/version.json"); // as CheckAsync builds its request
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
            var lines = (await server).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(new[] { "GET /version.json HTTP/1.1", $"Host: 127.0.0.1:{port}" }, lines); // no User-Agent, no Accept-Encoding, nothing else
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_no_other_server_can_answer_for_dictum_kynni_au()
    {
        // Lens probe (16/10/2026): through the model-download client, a 301 from "dictum.kynni.au" to another host was followed
        // inside the socket handler, after the offline_only guard, and that host's "99.0.0 is available." was shown. This is the
        // check's own client as production builds it, with the address changed between its guard and its real socket handler.
        // Both servers are listeners on this PC, so even a followed redirect stays on it.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        const string elsewhere = """{"version":"99.0.0","notes":"Served by another host."}""";
        var other = OfflineGuardTests.Serve($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {elsewhere.Length}\r\nConnection: close\r\n\r\n{elsewhere}", cts.Token);
        var site = OfflineGuardTests.Serve($"HTTP/1.1 301 Moved Permanently\r\nLocation: http://127.0.0.1:{other.Port}/version.json\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cts.Token);
        using (var http = UpdateClient())
        {
            var guard = Guard(http);
            // The socket handler as production built it, before its address is changed: TLS 1.2 as every download has, and no redirect followed.
            var sockets = Assert.IsType<SocketsHttpHandler>(guard.InnerHandler);
            Assert.Equal(SslProtocols.Tls12, sockets.SslOptions.EnabledSslProtocols);
            Assert.False(sockets.AllowAutoRedirect);
            guard.InnerHandler = new ToListener(site.Port, guard.InnerHandler!);
            var result = await Check(http);
            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Equal(ProposedFailedLine, result.Line);
        }
        Assert.Equal(1, site.Connections());
        Assert.Equal(0, other.Connections()); // the redirect was not followed
        cts.Cancel();
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0", UpdateStatus.NewerAvailable, "1.1.0")]
    [InlineData("1.1.0+3e86363", "1.1.0", UpdateStatus.Latest, "1.1.0")] // the +sha suffix is ignored
    [InlineData("1.2.0", "1.1.0", UpdateStatus.Latest, "1.2.0")] // a build newer than the release is the latest
    [InlineData("1.0.0", "1.0.0", UpdateStatus.Latest, "1.0.0")]
    [InlineData("1.0.9", "1.0.10", UpdateStatus.NewerAvailable, "1.0.10")] // numbers, not text: 10 comes after 9
    [InlineData("1.0.0", "1.1.0+3e86363", UpdateStatus.NewerAvailable, "1.1.0")]
    [InlineData("1.1", "1.1.0", UpdateStatus.Latest, "1.1")] // a missing part counts as 0
    public async Task Versions_compare_on_their_numeric_part(string running, string published, UpdateStatus status, string shown)
    {
        var site = new FakeSite(HttpStatusCode.OK, $$"""{"version":"{{published}}","notes":"Notes.","published":"2026-10-16"}""");
        using var http = Client(site);
        var result = await Check(http, running);
        Assert.Equal(status, result.Status);
        Assert.Equal(status == UpdateStatus.NewerAvailable ? ProposedNewerLine(shown) : ProposedLatestLine(shown), result.Line);
        Assert.Single(site.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"version":""")] // cut short: not JSON
    [InlineData(HttpStatusCode.OK, "<html>Not found</html>")]
    [InlineData(HttpStatusCode.OK, """["1.1.0"]""")] // not an object
    [InlineData(HttpStatusCode.OK, """{"notes":"No version here."}""")]
    [InlineData(HttpStatusCode.OK, """{"version":"banana"}""")]
    [InlineData(HttpStatusCode.OK, """{"version":110}""")] // a number, not a version
    [InlineData(HttpStatusCode.OK, """{"version":"1.1.0.0.0"}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"version":"99.0.0"}""")] // a newer version inside an error is still an error
    [InlineData(HttpStatusCode.NotFound, """{"version":"99.0.0"}""")]
    public async Task A_bad_answer_says_it_could_not_check_and_is_not_retried(HttpStatusCode status, string body)
    {
        var site = new FakeSite(status, body);
        using var http = Client(site);
        var result = await Check(http);
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Equal(ProposedFailedLine, result.Line);
        Assert.Single(site.Requests); // one request, no retry
    }

    [Fact]
    public async Task Fields_the_check_does_not_know_are_ignored_and_the_notes_come_with_a_newer_version()
    {
        var site = new FakeSite(HttpStatusCode.OK, """{"published":"2026-11-01","version":"1.1.0","channel":"stable","notes":"  Faster start-up.  ","sha256":{"zip":"abc"}}""");
        using var http = Client(site);
        var result = await Check(http);
        Assert.Equal(UpdateStatus.NewerAvailable, result.Status);
        Assert.Equal("1.1.0", result.Version);
        Assert.Equal("Faster start-up.", result.Notes);
    }

    [Fact]
    public async Task A_version_file_saved_with_a_byte_order_mark_still_reads()
    {
        byte[] body = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"version":"1.1.0"}""")];
        var site = new FakeSite(HttpStatusCode.OK, () => new ByteArrayContent(body));
        using var http = Client(site);
        Assert.Equal(UpdateStatus.NewerAvailable, (await Check(http)).Status);
    }

    /// <summary>The most of an answer the check reads: 16 KiB, pinned here as a number so a change to it is deliberate.</summary>
    private const int SizeLimit = 16 * 1024;

    [Theory]
    [InlineData(SizeLimit, true, UpdateStatus.NewerAvailable)]
    [InlineData(SizeLimit, false, UpdateStatus.NewerAvailable)]
    [InlineData(SizeLimit + 1, true, UpdateStatus.Failed)]
    [InlineData(SizeLimit + 1, false, UpdateStatus.Failed)]
    public async Task At_most_16_KiB_of_an_answer_is_read(int bytes, bool contentLength, UpdateStatus status)
    {
        const string head = "{\"version\":\"99.0.0\",\"notes\":\"", tail = "\"}";
        var body = Encoding.UTF8.GetBytes(head + new string('x', bytes - head.Length - tail.Length) + tail);
        Assert.Equal(bytes, body.Length);
        // With Content-Length the size is known up front; without it (as a chunked answer arrives) the read stops at the limit.
        var site = new FakeSite(HttpStatusCode.OK, () => contentLength ? new ByteArrayContent(body) : new StreamContent(new NoLengthStream(body)));
        using var http = Client(site);
        Assert.Equal(status, (await Check(http)).Status);
    }

    /// <summary>1,024 times the limit: far more than the check may read.</summary>
    private const int Overlong = 1024 * SizeLimit;

    [Fact]
    public async Task An_answer_without_a_length_is_read_only_up_to_the_limit_however_long_it_goes_on()
    {
        // The cases above end in Failed whether the check stops at the limit or reads the whole answer and only then measures
        // it (the re-lens, 16/10/2026), so the answer alone cannot pin the stop. Here a listener on this PC sends 16 MiB with no
        // Content-Length to the check's own client over its real socket handler, and what is pinned is how much the check read.
        // That is counted where it reads (CountsReads), not at the server: 15 runs on 16/10/2026 had the server hand 32,768 to
        // 245,760 bytes to its socket before the check hung up, because the sockets buffer what is sent before anything is read.
        const string head = "{\"version\":\"99.0.0\",\"notes\":\"", tail = "\"}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = client.GetStream();
                if (await stream.ReadAsync(new byte[8192], cts.Token) == 0) return; // the request (small); nothing to answer if the client left
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n" + head), cts.Token);
                var filler = new byte[SizeLimit];
                Array.Fill(filler, (byte)'x');
                for (var sent = 0; sent < Overlong; sent += filler.Length) await stream.WriteAsync(filler, cts.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(tail), cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // the check hung up, or the test ended
            }
        }, cts.Token);
        try
        {
            using var http = UpdateClient();
            var guard = Guard(http);
            var reads = new CountsReads(guard.InnerHandler!);
            guard.InnerHandler = new ToListener(((IPEndPoint)listener.LocalEndpoint).Port, reads);
            var result = await Check(http, timeout: TimeSpan.FromSeconds(2)); // only a backstop: a check that stops at the limit is done in milliseconds
            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Equal(SizeLimit + 1, reads.Read); // one byte past the limit is what shows the answer is over it; nothing past that is read
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task A_check_with_no_answer_gives_up_at_its_timeout_and_does_not_retry()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), UpdateCheck.CheckTimeout); // README: "gives up after 10 seconds"
        var site = new SilentSite();
        using var http = Client(site);
        var check = Check(http, timeout: TimeSpan.FromMilliseconds(200));
        Assert.Same(check, await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10), Ct))); // it ends by itself
        var result = await check;
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Equal(ProposedFailedLine, result.Line);
        Assert.Equal(1, site.Requests);
    }

    [Fact]
    public async Task Offline_only_sends_nothing_and_says_so()
    {
        var site = new FakeSite(HttpStatusCode.OK, LiveFile);
        using var http = new HttpClient(site); // no guard underneath: the check itself must not ask
        var result = await Check(http, offlineOnly: true);
        Assert.Empty(site.Requests);
        Assert.Equal(UpdateStatus.OfflineOnly, result.Status);
        Assert.Equal(ProposedOfflineOnlyLine, result.Line);
    }

    [Fact]
    public async Task Offline_only_switched_on_as_the_request_leaves_is_refused_by_the_guard_and_says_so()
    {
        var site = new FakeSite(HttpStatusCode.OK, LiveFile);
        var reads = 0;
        bool Offline() => Interlocked.Increment(ref reads) > 1; // off when the check looks, on when the guard looks
        using var http = Client(site, Offline);
        var result = await UpdateCheck.CheckAsync(http, "1.0.0", packaged: false, Offline, NullLog.Instance, Ct);
        Assert.Equal(2, reads);
        Assert.Empty(site.Requests);
        Assert.Equal(UpdateStatus.OfflineOnly, result.Status);
        Assert.Equal(ProposedOfflineOnlyLine, result.Line);
    }

    [Fact]
    public async Task The_Store_build_never_checks()
    {
        var site = new FakeSite(HttpStatusCode.OK, """{"version":"99.0.0"}""");
        using var http = new HttpClient(site); // no guard underneath: the check itself must refuse
        var result = await Check(http, packaged: true);
        Assert.Empty(site.Requests);
        Assert.Equal(UpdateStatus.StoreBuild, result.Status);
        Assert.Equal(ProposedStoreLine, result.Line);
    }

    [Fact]
    public void The_site_s_version_file_is_one_the_app_can_read()
    {
        var json = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "version.json"));
        var result = UpdateCheck.Interpret(json, "0.0.1");
        Assert.Equal(UpdateStatus.NewerAvailable, result.Status); // its "version" is a version number
        Assert.False(string.IsNullOrEmpty(result.Notes));
    }

    // ---- Settings > About ----------------------------------------------------------------------------------------------

    private static IEnumerable<T> All<T>(ILogical root) => root.GetLogicalDescendants().OfType<T>();

    private static Button? FindButton(ILogical root, string label) => All<Button>(root).FirstOrDefault(b => b.Content as string == label);

    /// <summary>Clicks a button the way a user can: only when it is shown and enabled.</summary>
    private static void Click(ILogical root, string label)
    {
        var button = FindButton(root, label);
        Assert.True(button is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }, $"\"{label}\" is not there to press.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static List<string> VisibleLines(ILogical root) => [.. All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

    /// <summary>Runs the UI thread's queued work until <paramref name="condition"/> holds.</summary>
    private static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>The version the About page shows ("1.0.0"), read here rather than from the app's helper.</summary>
    private static string RunningVersion => typeof(AppShell).Assembly.GetName().Version!.ToString(3);

    public enum Answer { Latest, Newer, Failure, OfflineOnly }

    [Theory]
    [InlineData(Answer.Latest)]
    [InlineData(Answer.Newer)]
    [InlineData(Answer.Failure)]
    [InlineData(Answer.OfflineOnly)]
    public Task The_About_page_shows_each_result_word_for_word(Answer answer) => UiSession.Run(() =>
    {
        var (status, body, expected) = answer switch
        {
            Answer.Latest => (HttpStatusCode.OK, $$"""{"version":"{{RunningVersion}}","notes":"Nothing new."}""", ProposedLatestLine(RunningVersion)),
            // The file names an address of its own: the button must still open the fixed one.
            Answer.Newer => (HttpStatusCode.OK, """{"version":"99.1.0","notes":"Faster start-up.","url":"https://example.invalid/not-the-download-page"}""", ProposedNewerLine("99.1.0")),
            Answer.Failure => (HttpStatusCode.InternalServerError, """{"version":"99.1.0"}""", ProposedFailedLine),
            _ => (HttpStatusCode.OK, """{"version":"99.1.0"}""", ProposedOfflineOnlyLine),
        };
        var site = new FakeSite(status, body);
        var downloads = new CountingHandler(); // the model-download client's network: the check has its own client, so nothing lands here
        using var scratch = new Scratch(answer == Answer.OfflineOnly ? """{ "hotkey": "F24", "offline_only": true }""" : """{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell(downloadHandler: downloads, updateHandler: site);
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Assert.Contains(ProposedPrivacyLine, VisibleLines(window)); // beside the button, before any check
        Assert.DoesNotContain(expected, VisibleLines(window));
        Assert.False(FindButton(window, ProposedDownloadPageButton)?.IsEffectivelyVisible ?? false);

        Click(window, ProposedButton);
        Assert.True(Eventually(() => VisibleLines(window).Contains(expected)), $"the About page never showed \"{expected}\"");
        Assert.Equal(answer == Answer.OfflineOnly ? 0 : 1, site.Requests.Count);
        Assert.Equal(0, downloads.Requests);
        if (answer == Answer.Newer)
        {
            // The line, then the button, then the release notes under it.
            var order = All<Control>(window).ToList();
            var line = order.FindIndex(c => c is TextBlock t && t.Text == expected);
            var button = order.FindIndex(c => c is Button b && b.Content as string == ProposedDownloadPageButton);
            var notes = order.FindIndex(c => c is TextBlock { Text: "Faster start-up.", IsEffectivelyVisible: true });
            Assert.True(line >= 0 && line < button && button < notes, $"line {line}, button {button}, notes {notes}");
            Click(window, ProposedDownloadPageButton);
            Assert.Equal(new[] { DownloadPage }, platform.Opened); // a fixed address, never one from the file
        }
        else
        {
            Assert.False(FindButton(window, ProposedDownloadPageButton)?.IsEffectivelyVisible ?? false);
            Assert.DoesNotContain("Nothing new.", VisibleLines(window)); // notes come only with a newer version
            Assert.Empty(platform.Opened);
        }
        window.Close();
        shell.Dispose();
    });

    [Fact]
    public Task Pressing_the_button_again_while_a_check_runs_sends_no_second_request() => UiSession.Run(() =>
    {
        var site = new GateSite($$"""{"version":"{{RunningVersion}}"}""");
        var downloads = new CountingHandler();
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell(downloadHandler: downloads, updateHandler: site);
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        try
        {
            Click(window, ProposedButton);
            Assert.True(Eventually(() => site.Requests == 1), "the first press sent nothing");
            Click(window, ProposedButton); // still checking: ignored
            site.Release();
            Assert.True(Eventually(() => VisibleLines(window).Contains(ProposedLatestLine(RunningVersion))), "the check never finished");
            Assert.Equal(1, site.Requests);
            Assert.Equal(0, downloads.Requests);
        }
        finally
        {
            site.Release(); // never leave a request waiting
            window.Close();
            shell.Dispose();
        }
    });

    [Fact]
    public Task A_new_press_clears_the_last_result_until_its_answer_arrives() => UiSession.Run(() =>
    {
        // Lens (16/10/2026): pressing again after "Could not check right now." left that line up while the new check ran, so
        // the press looked like it did nothing.
        var site = new GateSite($$"""{"version":"{{RunningVersion}}"}""", failFirst: true);
        var downloads = new CountingHandler();
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, _, _) = scratch.Shell(downloadHandler: downloads, updateHandler: site);
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        try
        {
            Click(window, ProposedButton);
            Assert.True(Eventually(() => VisibleLines(window).Contains(ProposedFailedLine)), "the first check never failed");
            Click(window, ProposedButton);
            Assert.True(Eventually(() => site.Requests == 2), "the second press sent nothing");
            Assert.DoesNotContain(ProposedFailedLine, VisibleLines(window)); // the answer is still held back: the old line is gone
            site.Release();
            Assert.True(Eventually(() => VisibleLines(window).Contains(ProposedLatestLine(RunningVersion))), "the second check never finished");
            Assert.Equal(0, downloads.Requests);
        }
        finally
        {
            site.Release(); // never leave a request waiting
            window.Close();
            shell.Dispose();
        }
    });

    [Fact]
    public async Task The_Store_build_has_no_check_button_and_says_the_Store_updates_it() => await UiSession.RunAsync(async () =>
    {
        var site = new FakeSite(HttpStatusCode.OK, """{"version":"99.1.0"}""");
        var downloads = new CountingHandler();
        using var scratch = new Scratch("""{ "hotkey": "F24" }""");
        var (shell, platform, _) = scratch.Shell(downloadHandler: downloads, updateHandler: site);
        platform.FakePackage.IsPackaged = true;
        shell.Reload(userRequested: false);
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage("About");
        Assert.Contains(ProposedStoreLine, VisibleLines(window));
        Assert.Null(FindButton(window, ProposedButton));
        Assert.Null(FindButton(window, ProposedDownloadPageButton));
        Assert.DoesNotContain(ProposedPrivacyLine, VisibleLines(window));
        window.Close();

        // Nor can the shell be made to check: no request is possible from the Store build.
        var result = await shell.CheckForUpdatesAsync(Ct);
        Assert.Equal(UpdateStatus.StoreBuild, result.Status);
        Assert.Empty(site.Requests);
        Assert.Equal(0, downloads.Requests);
        shell.Dispose();
        return true;
    });

    [Fact]
    public Task A_model_download_goes_through_the_download_client_and_never_the_check_s_own() => UiSession.Run(() =>
    {
        // The About-page tests above put a counting handler under the download client, so a check sent through it is seen. This is
        // the other way round: one under the check's own client, so a model download sent through it is seen. The two clients differ
        // where it matters (the download client follows redirects, as huggingface.co's CDN needs; the check's own follows none).
        var downloads = new CountingHandler();
        var updates = new CountingHandler();
        using var scratch = new Scratch();
        var (shell, _, dialogs) = scratch.Shell(downloadHandler: downloads, updateHandler: updates);
        dialogs.ConfirmDownloads = true; // the user presses Download
        try
        {
            var ended = new List<bool>();
            var spec = new DownloadSpec(new Uri("https://dictate-test.invalid/a.bin"), "a.bin", 4, null); // .invalid can never resolve (RFC 2606)
            shell.Models.ApplyWithDownloads("A test download", [(spec, Path.Combine(scratch.Root.FullName, "a.bin"))], [], finished: ended.Add);
            Assert.True(Eventually(() => ended.Count == 1), "the download never ended");
            Assert.Equal([false], ended); // the counting handler answers 403, so the download fails at once
            Assert.Equal(1, downloads.Requests);
            Assert.Equal(0, updates.Requests);
        }
        finally
        {
            shell.Dispose(); // also stops a download still running
        }
    });

    // ---- The site --------------------------------------------------------------------------------------------------------

    /// <summary>A page of the site without its HTML comments: a sentence only inside a comment is not said to the reader.</summary>
    private static string VisibleHtml(string page) =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", page)), "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>The text of the paragraph that directly follows <paramref name="heading"/>.</summary>
    private static string ParagraphAfter(string html, string heading)
    {
        var start = html.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the page no longer has {heading}");
        var rest = html[(start + heading.Length)..].TrimStart();
        Assert.StartsWith("<p>", rest);
        return rest["<p>".Length..rest.IndexOf("</p>", StringComparison.Ordinal)];
    }

    [Fact]
    public void The_privacy_policy_says_when_Dictum_reads_dictum_kynni_au()
    {
        var html = VisibleHtml("privacy.html");
        Assert.Equal(ProposedSiteSentence, ParagraphAfter(html, "<h3>This website: <code>dictum.kynni.au</code></h3>"));
        Assert.Contains(ProposedShortVersionBullet, html);
        Assert.DoesNotContain("no update check", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_landing_page_card_for_dictum_kynni_au_says_the_same()
    {
        var html = VisibleHtml("index.html");
        Assert.Equal(ProposedSiteSentence, ParagraphAfter(html, """<h3 class="host">dictum.kynni.au</h3>"""));
        Assert.DoesNotContain("no update check", html, StringComparison.OrdinalIgnoreCase);
    }
}
