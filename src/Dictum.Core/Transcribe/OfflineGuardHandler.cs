using System.Net;

namespace Dictum.Transcribe;

/// <summary>
/// The single network choke point for offline_only: every HttpClient Dictum creates (model downloads, the
/// OpenAI-compatible and Anthropic providers) sends through this handler, which refuses any request to a host that
/// is not on this machine while the setting is on. The per-feature checks stay for their clearer messages; this is
/// the backstop that holds even if one of them is missed. Read on every request, so it follows settings reloads.
/// </summary>
/// <remarks>
/// Redirects are followed inside the inner <see cref="SocketsHttpHandler"/> and are not seen here, so the provider client
/// (<see cref="CreateClient"/>) does not follow them at all.
/// </remarks>
public sealed class OfflineGuardHandler(Func<bool> offlineOnly, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (offlineOnly() && request.RequestUri is { } uri && !uri.IsLoopback)
        {
            throw new OfflineOnlyException($"A request to {uri.Host}");
        }
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// An HttpClient for the cleanup providers, guarded the same way; callers bound each request themselves. Redirects
    /// are not followed: they would be followed inside the socket handler, after this guard, so a server on this
    /// machine could forward a transcript off it (lens probe, 15/10/2026). A 3xx is an HTTP error and the cleanup
    /// falls back to the rule-based tidy. The download client keeps following redirects (the CDN needs them) and is
    /// refused outright under offline_only.
    /// </summary>
    public static HttpClient CreateClient(Func<bool> offlineOnly) =>
        new(new OfflineGuardHandler(offlineOnly, new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = LoopbackDirectProxy.Default }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

/// <summary>
/// The proxy every Dictum HttpClient uses: a request to this machine (localhost, 127.0.0.1, ::1) always goes direct, and
/// everything else follows Windows' or the environment's proxy settings as before. The guard above checks only the
/// request's URI, so without this a configured proxy (HTTP_PROXY, or a Windows proxy whose bypass list lacks
/// &lt;local&gt;) received a "loopback" request in full, transcript, prompt and LLM_API_KEY included (lens probe, 15/10/2026).
/// </summary>
/// <remarks>
/// Decision (orchestrator, 15/10/2026): a remote cleanup gateway reached through a corporate proxy is not a supported
/// configuration; the proxy stays in use for other hosts only so model downloads keep working behind one.
/// </remarks>
public sealed class LoopbackDirectProxy(IWebProxy inner) : IWebProxy
{
    /// <summary>Wraps the process default (read when a client is created).</summary>
    public static LoopbackDirectProxy Default => new(HttpClient.DefaultProxy);

    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination) => destination.IsLoopback ? null : inner.GetProxy(destination);

    public bool IsBypassed(Uri host) => host.IsLoopback || inner.IsBypassed(host);
}
