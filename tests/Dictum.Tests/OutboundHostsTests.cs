using System.Text.RegularExpressions;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// The privacy policy names every address Dictum can connect to, and no other (legal block, APPROVED by the owner 23/10/2026 09:50 AEDT; build
/// item 11). The hosts are read from the constants the app connects with: the model download host, the Check for updates file, Anthropic's
/// default address, and every clean-up and cloud speech preset that is not on this computer. A new host in any of them fails
/// <see cref="The_app_connects_to_exactly_the_hosts_the_policy_lists"/> until it is added to <see cref="Hosts"/>, and then
/// <see cref="The_privacy_policy_names_every_host_the_app_connects_to"/> until site/privacy.html names it. Mutations (observed failing,
/// 23/10/2026, one at a time): api.groq.com taken off the page; a fake host, api.example-cloud.com, added to the page; api.groq.com taken out of
/// <see cref="Hosts"/>.
/// </summary>
public sealed class OutboundHostsTests
{
    /// <summary>The complete outbound list. Azure OpenAI's address carries the customer's resource name, so it is the suffix the preset matches.</summary>
    private static readonly string[] Hosts =
    [
        "api.anthropic.com", "api.groq.com", "api.openai.com", "dictum.kynni.au", "generativelanguage.googleapis.com", "huggingface.co", "openai.azure.com",
    ];

    /// <summary>
    /// Hosts the policy names that Dictum itself never connects to by a constant: each is a link or a name for the reader. us.aws.cdn.hf.co is
    /// where huggingface.co hands a download on (the policy says so and dates it); it is reached by following Hugging Face's redirect, not named
    /// in the code.
    /// </summary>
    private static readonly string[] NamedButNotConnectedTo =
    [
        "us.aws.cdn.hf.co", "github.com", "www.kynni.com.au", "vercel.com", "ico.org.uk", "www.oaic.gov.au", "oaic.gov.au", "nspasteboard.org",
    ];

    /// <summary>The hosts the app's own constants point at, off this computer.</summary>
    private static SortedSet<string> FromCode()
    {
        var hosts = new SortedSet<string>(StringComparer.Ordinal)
        {
            ModelCatalog.DownloadHost,
            new Uri(UpdateCheck.VersionUrl).Host,
            new Uri(AnthropicLlmClient.DefaultBaseUrl).Host,
        };
        foreach (var preset in CloudPresets.All.Concat(SpeechPresets.All))
        {
            if (preset.HostSuffix is { } suffix) hosts.Add(suffix.TrimStart('.'));
            else if (preset.BaseUrl is { } url && !new Uri(url).IsLoopback) hosts.Add(new Uri(url).Host);
        }
        return hosts;
    }

    /// <summary>site/privacy.html without its HTML comments: a host named only in a comment does not count.</summary>
    private static string Policy() =>
        Regex.Replace(File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "site", "privacy.html")), "<!--.*?-->", "", RegexOptions.Singleline);

    [Fact]
    public void The_app_connects_to_exactly_the_hosts_the_policy_lists()
    {
        Assert.Equal(Hosts.Order(StringComparer.Ordinal), FromCode());
        Assert.DoesNotContain(CloudPresets.All.Concat(SpeechPresets.All), p => p.BaseUrl is { } u && new Uri(u).Scheme == Uri.UriSchemeHttp && !new Uri(u).IsLoopback);
    }

    [Fact]
    public void The_privacy_policy_names_every_host_the_app_connects_to()
    {
        var policy = Policy();
        foreach (var host in FromCode())
            Assert.True(policy.Contains(host, StringComparison.Ordinal), $"site/privacy.html does not name {host}, which Dictum connects to");
    }

    /// <summary>
    /// Every host-like name on the page (visible text and links; an e-mail address is not a host, and a file name such as settings.json is not
    /// one either) is a host the app connects to, or one listed in <see cref="NamedButNotConnectedTo"/>.
    /// </summary>
    [Fact]
    public void The_privacy_policy_names_no_host_the_app_does_not_connect_to()
    {
        string[] fileExtensions = ["json", "txt", "env", "tmp", "log", "wav", "exe", "app", "cs", "css", "svg", "html", "xml", "md", "zip", "ps1"];
        // A name starts where no letter, digit, "@" or "-" comes before it, nor such a character and a dot (so "hello@kynni.com.au" yields no
        // "com.au"); "{resource}.openai.azure.com" still yields openai.azure.com.
        var named = Regex.Matches(Policy(), @"(?<![\w@-])(?<![\w@-]\.)(?:[a-z0-9-]+\.)+[a-z]{2,}(?![\w-])")
            .Select(m => m.Value)
            .Where(h => !fileExtensions.Contains(h[(h.LastIndexOf('.') + 1)..], StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("api.anthropic.com", named); // the scan really reads hosts
        Assert.Contains("openai.azure.com", named); // and the one written after {resource}.
        var known = FromCode().Concat(NamedButNotConnectedTo).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(named.Where(h => !known.Contains(h)).Order(StringComparer.Ordinal));
    }
}
