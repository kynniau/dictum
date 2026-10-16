using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dictum.Core;

namespace Dictum.Transcribe;

/// <summary>What one press of Check for updates found.</summary>
public enum UpdateStatus
{
    /// <summary>This copy is the version version.json names, or newer.</summary>
    Latest,

    /// <summary>version.json names a newer version.</summary>
    NewerAvailable,

    /// <summary>offline_only is on, so nothing was requested.</summary>
    OfflineOnly,

    /// <summary>No answer in time, an HTTP error, or an answer that is not a version.json Dictum can read.</summary>
    Failed,

    /// <summary>The Microsoft Store build: it never checks, because the Store updates it.</summary>
    StoreBuild,
}

/// <summary>One check's result.</summary>
/// <param name="Version">Latest: this copy's version. NewerAvailable: the newer one. The numeric part only ("1.1.0").</param>
/// <param name="Notes">NewerAvailable: the release notes version.json gives, if any.</param>
/// <param name="Reason">Failed: why, for the log only; Settings shows <see cref="UpdateCheck.FailedLine"/>.</param>
public sealed record UpdateCheckResult(UpdateStatus Status, string? Version = null, string? Notes = null, string? Reason = null)
{
    /// <summary>The line Settings → About shows for this result.</summary>
    public string Line => Status switch
    {
        UpdateStatus.Latest => UpdateCheck.LatestLine(Version ?? "?"),
        UpdateStatus.NewerAvailable => UpdateCheck.NewerLine(Version ?? "?"),
        UpdateStatus.OfflineOnly => UpdateCheck.OfflineOnlyLine,
        UpdateStatus.StoreBuild => UpdateCheck.StoreLine,
        _ => UpdateCheck.FailedLine,
    };
}

/// <summary>
/// Check for updates (decision 11, 16/10/2026): the direct download's Settings → About asks whether a newer version is
/// out, only when the user presses the button; nothing is automatic. One GET of <see cref="VersionUrl"/> over HTTPS
/// through the model-download client (<see cref="ModelDownloader.CreateHttpClient"/>: the offline_only guard, TLS 1.2,
/// no proxy for this machine), with no query string and no header of Dictum's own, so nothing about the user is sent.
/// No retry; <see cref="CheckTimeout"/> bounds the whole request. The Store build never checks (the Store updates it),
/// and offline_only refuses before anything is sent.
/// </summary>
public static partial class UpdateCheck
{
    /// <summary>The one file the check reads, for example {"version":"1.0.0","notes":"…","published":"2026-10-16"}; other fields are ignored.</summary>
    public const string VersionUrl = "https://dictum.kynni.au/version.json";

    /// <summary>Where "Open the download page" goes. Fixed here: an address inside version.json is never opened.</summary>
    public const string DownloadPageUrl = "https://github.com/kynniau/dictum/releases/latest";

    /// <summary>How long one check may take, connecting included. Chosen 16/10/2026, not measured: the file was 76 bytes.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The most of an answer that is read. version.json was 76 bytes on 16/10/2026; a bigger answer is not it.</summary>
    public const int MaxBytes = 16 * 1024;

    // The user-facing strings: proposed 16/10/2026, awaiting the product owner's approval; do not reword.
    // UpdateCheckTests pins each one word for word.

    /// <summary>The button on Settings → About (direct download only). Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string ButtonLabel = "Check for updates";

    /// <summary>Beside the button. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string PrivacyLine = "Checking reads one small file from dictum.kynni.au and sends nothing about you.";

    /// <summary>This copy is the latest. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public static string LatestLine(string version) => $"You have the latest version ({version}).";

    /// <summary>A newer version is out. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public static string NewerLine(string version) => $"{version} is available.";

    /// <summary>The button under <see cref="NewerLine"/>, above the release notes. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string DownloadPageLabel = "Open the download page";

    /// <summary>offline_only refused the check. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string OfflineOnlyLine = "Offline only is on, so Dictum did not check.";

    /// <summary>No network, an HTTP error or a bad file. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string FailedLine = "Could not check right now. Try again later.";

    /// <summary>The Store build, in place of the button. Proposed 16/10/2026, awaiting the product owner's approval; do not reword.</summary>
    public const string StoreLine = "Updates come from the Microsoft Store.";

    /// <summary>
    /// One check. A network or file problem never throws: the result says what happened and the reason is logged. Only the
    /// caller's own cancellation throws.
    /// </summary>
    /// <param name="http">The model-download client: its guard refuses the request too if offline_only comes on as it leaves.</param>
    /// <param name="runningVersion">This copy's version ("1.0.0"; a "+sha" suffix is ignored).</param>
    /// <param name="packaged">The Store build: nothing is ever requested.</param>
    /// <param name="offlineOnly">Read before anything is sent.</param>
    /// <param name="timeout">Tests shorten it; <see cref="CheckTimeout"/> otherwise.</param>
    public static async Task<UpdateCheckResult> CheckAsync(HttpClient http, string runningVersion, bool packaged, Func<bool> offlineOnly, ILog log,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (packaged) return Logged(log, new UpdateCheckResult(UpdateStatus.StoreBuild), runningVersion);
        if (offlineOnly()) return Logged(log, new UpdateCheckResult(UpdateStatus.OfflineOnly), runningVersion);
        var limit = timeout ?? CheckTimeout;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(limit);
        UpdateCheckResult result;
        try
        {
            // Nothing is added to the request: no query string and no header. On the wire it is the request line and Host alone
            // (UpdateCheckTests reads it from a listener on this PC).
            using var request = new HttpRequestMessage(HttpMethod.Get, VersionUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            result = !response.IsSuccessStatusCode ? Failed($"HTTP {(int)response.StatusCode}")
                : await ReadAsync(response.Content, bounded.Token).ConfigureAwait(false) is { } json ? Interpret(json, runningVersion)
                : Failed($"the answer is over {MaxBytes:N0} bytes");
        }
        catch (OfflineOnlyException)
        {
            result = new UpdateCheckResult(UpdateStatus.OfflineOnly); // offline_only came on as the request left: the guard refused it
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Failed($"no answer within {limit.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            result = Failed(ModelDownloader.Describe(ex));
        }
        return Logged(log, result, runningVersion);
    }

    /// <summary>The answer as text, or null when it is longer than <see cref="MaxBytes"/> (nothing past that is read).</summary>
    private static async Task<string?> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0) length += read;
        return length > MaxBytes ? null : Encoding.UTF8.GetString(buffer, 0, length).TrimStart((char)0xFEFF); // a file saved with a byte order mark still reads
    }

    /// <summary>
    /// Compares version.json's "version" with <paramref name="runningVersion"/>, numeric parts only. Other fields are ignored;
    /// anything but a JSON object whose "version" is a version number is a failure.
    /// </summary>
    internal static UpdateCheckResult Interpret(string json, string runningVersion)
    {
        if (NumericVersion(runningVersion) is not { } running) return Failed($"this copy's version \"{runningVersion}\" is not a version number");
        string? published;
        string? notes;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failed("version.json is not a JSON object");
            published = root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            notes = root.TryGetProperty("notes", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()?.Trim() : null;
        }
        catch (JsonException ex)
        {
            return Failed($"version.json is not JSON ({ex.Message})");
        }
        if (NumericVersion(published) is not { } latest) return Failed("version.json has no version number in \"version\"");
        return running.Value < latest.Value
            ? new UpdateCheckResult(UpdateStatus.NewerAvailable, latest.Text, string.IsNullOrEmpty(notes) ? null : notes)
            : new UpdateCheckResult(UpdateStatus.Latest, running.Text);
    }

    /// <summary>
    /// The numeric part of a version: "1.1.0" from "1.1.0", "1.1.0+3e86363" or "1.1.0-beta", compared with its missing parts as 0
    /// (so 1.1 is 1.1.0). Null unless the text is one to four dot-separated numbers, optionally followed by a +… or -… suffix.
    /// </summary>
    internal static (string Text, Version Value)? NumericVersion(string? text)
    {
        if (text is null || VersionPattern().Match(text) is not { Success: true } match) return null;
        var numbers = match.Groups[1].Value;
        var parts = numbers.Split('.').Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).ToList(); // at most 9 digits: always fits
        while (parts.Count < 4) parts.Add(0);
        return (numbers, new Version(parts[0], parts[1], parts[2], parts[3]));
    }

    // [0-9], not \d: \d also matches other scripts' digits, which int.Parse refuses.
    [GeneratedRegex(@"^\s*([0-9]{1,9}(?:\.[0-9]{1,9}){0,3})(?:[+-][0-9A-Za-z.+-]*)?\s*$")]
    private static partial Regex VersionPattern();

    private static UpdateCheckResult Failed(string reason) => new(UpdateStatus.Failed, Reason: reason);

    private static UpdateCheckResult Logged(ILog log, UpdateCheckResult result, string runningVersion)
    {
        switch (result.Status)
        {
            case UpdateStatus.Failed:
                log.Warn($"Check for updates failed: {result.Reason}.");
                break;
            case UpdateStatus.NewerAvailable:
                log.Info($"Check for updates: {result.Version} is available (this copy is {runningVersion}).");
                break;
            case UpdateStatus.Latest:
                log.Info($"Check for updates: this copy ({runningVersion}) is the latest.");
                break;
            case UpdateStatus.OfflineOnly:
                log.Info("Check for updates refused: offline_only is on, so nothing was sent.");
                break;
            default:
                log.Info("Check for updates: the Store build does not check; the Microsoft Store updates it.");
                break;
        }
        return result;
    }
}
