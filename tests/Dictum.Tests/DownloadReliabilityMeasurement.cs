using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using Dictum.Core;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// Measurement, not a check: downloads a real catalogue model through the production downloader several times with
/// one transport variant and records how many attempts each needed and why attempts failed. Gated by
/// DICTUM_DOWNLOAD_BENCH="&lt;speech model&gt;:&lt;default|tls12|http11|http2&gt;:&lt;runs&gt;"; results are appended to
/// DICTUM_DOWNLOAD_BENCH_OUT when set. Files go to a temporary folder and are deleted after each run.
/// </summary>
public class DownloadReliabilityMeasurement(ITestOutputHelper output)
{
    [Fact]
    public async Task Measure_download_attempts()
    {
        var config = TestEnv.Get("DOWNLOAD_BENCH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(config), "Set DICTUM_DOWNLOAD_BENCH=<model>:<variant>:<runs> (downloads real files).");
        var parts = config!.Split(':');
        var info = ModelCatalog.Find(parts[0]) ?? throw new ArgumentException($"unknown model {parts[0]}");
        var variant = parts[1];
        var runs = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        // variant: "default", or any of "tls12", "http11", "http2" combined (e.g. "tls12http2")
        var policy = variant.Contains("http2") ? DownloadPolicy.Default with { HttpVersion = HttpVersion.Version20 }
            : variant.Contains("http11") ? DownloadPolicy.Default with { HttpVersion = HttpVersion.Version11 }
            : DownloadPolicy.Default;
        using var http = ModelDownloader.CreateHttpClient(() => false, variant.Contains("tls12") ? SslProtocols.Tls12 : SslProtocols.None);
        var dir = Directory.CreateTempSubdirectory("dictate-dlbench-");
        var lines = new List<string>();
        try
        {
            for (var run = 1; run <= runs; run++)
            {
                var log = new RecordingLog();
                var target = Path.Combine(dir.FullName, ModelCatalog.FileName(info.Name));
                var sw = Stopwatch.StartNew();
                string outcome;
                try
                {
                    var attempts = await ModelDownloader.DownloadAsync(http, info.Download, target, false, new Progress<(long, long?)>(),
                        TestContext.Current.CancellationToken, log, policy);
                    outcome = $"ok after {attempts} attempt(s)";
                }
                catch (Exception ex)
                {
                    outcome = $"FAILED: {ex.GetType().Name}: {ex.Message}";
                }
                var secs = sw.Elapsed.TotalSeconds;
                var failures = log.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("failed at")).Select(l => l.Message).ToList();
                var version = log.Lines.Select(l => l.Message).LastOrDefault(m => m.Contains("HTTP/")) ?? "";
                lines.Add($"{DateTime.Now:dd/MM/yyyy HH:mm:ss} {info.Name} {variant} run {run}: {outcome}; {secs:0.0} s, {info.Bytes / 1e6 / secs:0.0} MB/s; {failures.Count} failed attempt(s); last response: {version}");
                lines.AddRange(failures.Select(f => "    " + f));
                if (File.Exists(target)) File.Delete(target);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
        foreach (var line in lines) output.WriteLine(line);
        if (TestEnv.Get("DOWNLOAD_BENCH_OUT") is { Length: > 0 } file) File.AppendAllLines(file, lines);
    }
}
