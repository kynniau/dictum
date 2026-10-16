using System.Net;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>offline_only at the single network choke point: the handler under every HttpClient Dictum creates.</summary>
public class OfflineGuardTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Theory]
    [InlineData("https://huggingface.co/x", true, false)]
    [InlineData("https://api.anthropic.com/v1/messages", true, false)]
    [InlineData("http://localhost:11434/v1/chat/completions", true, true)]
    [InlineData("http://127.0.0.1:1234/v1", true, true)]
    [InlineData("http://[::1]:8080/", true, true)]
    [InlineData("https://huggingface.co/x", false, true)]
    public async Task The_offline_guard_refuses_every_off_machine_request(string url, bool offline, bool allowed)
    {
        var inner = new CountingHandler();
        using var http = new HttpClient(new OfflineGuardHandler(() => offline, inner));
        if (allowed)
        {
            await http.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(1, inner.Requests);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<OfflineOnlyException>(() => http.GetAsync(url, TestContext.Current.CancellationToken));
            Assert.Contains("offline_only is on", ex.Message);
            Assert.Equal(0, inner.Requests);
        }
    }

    /// <summary>A one-shot HTTP server on 127.0.0.1 that answers every request with <paramref name="response"/>.</summary>
    internal static (int Port, Func<int> Connections, Task Serving) Serve(string response, CancellationToken ct)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    Interlocked.Increment(ref connections);
                    var stream = client.GetStream();
                    var buffer = new byte[64 * 1024];
                    if (await stream.ReadAsync(buffer, ct) == 0) continue; // the request (small); its content does not matter here
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }, ct);
        return (((IPEndPoint)listener.LocalEndpoint).Port, () => Volatile.Read(ref connections), serving);
    }

    [Fact]
    public async Task The_provider_client_does_not_follow_a_redirect_so_a_local_server_cannot_forward_the_transcript()
    {
        // Lens probe (15/10/2026): a loopback server answering 307 got the body forwarded, because redirects are followed
        // inside the socket handler, after the guard. The target here is a second loopback listener, so the test stays
        // off the network even when redirects are followed; what it checks is that no second request is made.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var target = Serve("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", cts.Token);
        var redirect = Serve($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/v1/chat/completions\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cts.Token);

        using var http = OfflineGuardHandler.CreateClient(() => true); // offline_only on: loopback is allowed
        var settings = new Dictum.Settings.LlmSettings { Provider = "openai-compatible", Model = "m", BaseUrl = $"http://127.0.0.1:{redirect.Port}/v1" };
        var cleaner = Dictum.Llm.TranscriptCleaner.Create(settings, offlineOnly: true, Dictum.Settings.SecretStore.FromContents("", null, _ => null), http,
            Dictum.Core.NullLog.Instance);
        var result = await cleaner.CleanAsync("um hello there", cts.Token);

        Assert.Equal(1, redirect.Connections());
        Assert.Equal(0, target.Connections()); // the redirect was not followed
        Assert.Equal("Hello there.", result.Text); // fell back to the rule-based tidy
        Assert.Contains("HTTP 307", result.FallbackReason);
        cts.Cancel();
    }

    [Fact]
    public async Task The_download_client_still_follows_a_redirect_as_the_model_CDN_needs()
    {
        // huggingface.co redirects model requests to its CDN (README, "Privacy"). The provider and Check for updates clients
        // follow no redirect; this pins that the download client still does. Both servers are listeners on this PC.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cdn = Serve("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 5\r\nConnection: close\r\n\r\nmodel", cts.Token);
        var hub = Serve($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{cdn.Port}/model.bin\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cts.Token);
        var (providers, downloads, updates) = Dictum.App.AppRules.CreateClients(() => false);
        using (providers)
        using (downloads)
        using (updates)
        {
            using var response = await downloads.GetAsync($"http://127.0.0.1:{hub.Port}/model.bin", cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("model", await response.Content.ReadAsStringAsync(cts.Token));
        }
        Assert.Equal(1, hub.Connections());
        Assert.Equal(1, cdn.Connections()); // the redirect was followed
        cts.Cancel();
    }

    [Fact]
    public async Task The_download_client_itself_is_guarded()
    {
        using var http = ModelDownloader.CreateHttpClient(() => true); // a real network handler: the guard must stop it first
        // dictate-test.invalid can never resolve (RFC 2606), so even a broken guard cannot reach a real host from a test.
        await Assert.ThrowsAsync<OfflineOnlyException>(() => http.GetAsync("https://dictate-test.invalid/model.bin", TestContext.Current.CancellationToken));
    }
}
