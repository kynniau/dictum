using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dictum.Llm;
using Dictum.Transcribe;
using Dictum.App;

namespace Dictum.Tests;

/// <summary>Tests that change process-wide state (HttpClient.DefaultProxy, environment variables) run on their own.</summary>
[CollectionDefinition("Process-wide state", DisableParallelization = true)]
public sealed class ProcessWideStateCollection;

/// <summary>Fix batch, item 1 (the Blocker), item 6 and the OpenTelemetry finding: nothing leaves through a side door.</summary>
[Collection("Process-wide state")]
public class FixBatchPrivacyTests
{
    /// <summary>A loopback listener that accepts connections, counts them and answers each with <paramref name="response"/>.</summary>
    private static (int Port, Func<int> Connections, TcpListener Listener) Listen(string response, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connections = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    Interlocked.Increment(ref connections);
                    var stream = client.GetStream();
                    var buffer = new byte[64 * 1024];
                    if (await stream.ReadAsync(buffer, ct) == 0) continue;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }, ct);
        return (((IPEndPoint)listener.LocalEndpoint).Port, () => Volatile.Read(ref connections), listener);
    }

    private const string Ok = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
    private const string BadGateway = "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task A_request_to_this_machine_never_goes_through_a_configured_proxy(string host)
    {
        // Lens probe (15/10/2026): with HTTP_PROXY set, offline_only on and base_url localhost, the transcript, the prompt
        // and LLM_API_KEY were sent to the proxy. Here a listener plays the proxy.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var proxy = Listen(BadGateway, cts.Token);
        var target = Listen(Ok, cts.Token);
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{proxy.Port}") { BypassProxyOnLocal = false };
        try
        {
            using var http = OfflineGuardHandler.CreateClient(() => true);
            using var body = new StringContent("""{"messages":[{"role":"user","content":"TRANSCRIPT"}]}""", Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"http://{host}:{target.Port}/v1/chat/completions", body, cts.Token);
            Assert.Equal(0, proxy.Connections()); // nothing reached the proxy
            Assert.Equal(1, target.Connections());
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
            cts.Cancel();
            proxy.Listener.Stop();
            target.Listener.Stop();
        }
    }

    [Fact]
    public async Task A_request_to_another_machine_still_uses_the_configured_proxy()
    {
        // The loopback bypass must not switch proxies off for model downloads and cloud providers: a corporate proxy that
        // Windows is configured with is still used for them (dictate-test.invalid never resolves, so the proxy is asked).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var proxy = Listen(BadGateway, cts.Token);
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{proxy.Port}") { BypassProxyOnLocal = false };
        try
        {
            using var http = OfflineGuardHandler.CreateClient(() => false);
            using var response = await http.GetAsync("http://dictate-test.invalid/v1/models", cts.Token);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal(1, proxy.Connections());
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
            cts.Cancel();
            proxy.Listener.Stop();
        }
    }

    private sealed class EndTurnHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"ok"}],
                     "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                    """, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task The_Anthropic_client_emits_no_spans_even_when_the_content_mode_variable_asks_for_them()
    {
        var recorded = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("com.anthropic", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (recorded) recorded.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE");
        Environment.SetEnvironmentVariable("ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE", "content");
        try
        {
            using var client = new AnthropicLlmClient("test-key-not-real", "claude-haiku-5-5", TimeSpan.FromSeconds(5), http: new HttpClient(new EndTurnHandler()));
            await client.CompleteAsync("SYS PROMPT", "TRANSCRIPT um hello there", TestContext.Current.CancellationToken);
            lock (recorded) Assert.Empty(recorded); // no span, so no transcript or prompt for an in-process exporter
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE", previous);
        }
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Deleted, ".env", null, true)] // deleting .env drops its key at once
    [InlineData(WatcherChangeTypes.Renamed, ".env.bak", ".env", true)] // so does renaming it away
    [InlineData(WatcherChangeTypes.Renamed, "settings.json", "settings.json.tmp", true)]
    [InlineData(WatcherChangeTypes.Deleted, "dictionary.txt", null, true)]
    [InlineData(WatcherChangeTypes.Changed, "corrections.txt", null, true)]
    [InlineData(WatcherChangeTypes.Changed, "snippets.txt", null, true)]
    [InlineData(WatcherChangeTypes.Deleted, "snippets.txt", null, true)] // deleting it switches snippets off at once
    [InlineData(WatcherChangeTypes.Deleted, "settings.json", null, false)] // documented: the settings in use stay
    [InlineData(WatcherChangeTypes.Changed, "notes.txt", null, false)]
    public void The_config_folder_watcher_reloads_on_the_right_changes(WatcherChangeTypes change, string name, string? oldName, bool reloads) =>
        Assert.Equal(reloads, AppRules.ReloadsOn(change, name, oldName));
}
