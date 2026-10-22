using System.Text;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.Tests;

/// <summary>
/// Settings > Models while cloud speech sends audio (owner wording, 22/10/2026 18:41 AEDT): the Advanced Speech model card's
/// introduction and the Simple view's explainer show the owner's replacement sentence only then, and the approved originals
/// byte for byte otherwise. Kept apart from CloudSpeechUiTests so that file stays under 1,000 lines (final lens on PR #54).
/// No request is made: the fake server refuses any that would be.
/// </summary>
public sealed class CloudSpeechModelsPageTests
{
    private const string SpeechHost = "speech.example.com";

    private static string SpeechAt() =>
        $$""", "speech": { "provider": "openai-compatible", "base_url": "https://{{SpeechHost}}/v1", "model": "speech-model" }""";

    private static void SpeechConsent(Scratch scratch) =>
        CloudConsentFile.WriteSpeech(scratch.Paths.ConsentFile, new CloudConsentRecord(SpeechSettings.OpenAiCompatible, SpeechHost, DateTimeOffset.UtcNow, "test"));

    private static (AppShell Shell, FakePlatform Platform, ScriptedDialogs Dialogs) Shell(Scratch scratch) => scratch.Shell(providerHandler: new NoRequests());

    /// <summary>Every visible line of a Settings page, drawn afresh from the settings and the steps in use.</summary>
    private static IReadOnlyList<string> PageLines(AppShell shell, string page)
    {
        var window = new SettingsWindow(shell);
        window.Show();
        window.ShowPage(page);
        var lines = window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        window.Close();
        return lines;
    }

    // Settings > Models' two "on this PC" sentences, approved originals, and the owner's replacements (22/10/2026 18:41 AEDT),
    // pinned byte for byte, independently of the app's copy.
    private const string ApprovedSpeechIntro = "Turns your voice into words, on this PC. Bigger is more accurate and slower.";
    private const string ReplacedSpeechIntro = "Turns your voice into words; with cloud speech on, speech.example.com does this and the model here is the fallback. Bigger is more accurate and slower.";
    private const string ApprovedExplainer = "Dictum turns your voice into text on this PC. The speech model does the listening. Standard works for most people. " +
                                             "If words come out wrong, try a more accurate model, or press Calibrate my voice to see which hears you best.";
    private const string ReplacedExplainer = "Dictum sends your voice to speech.example.com to be turned into text while cloud speech is on. The speech model does the listening. Standard works for most people. " +
                                             "If words come out wrong, try a more accurate model, or press Calibrate my voice to see which hears you best.";

    /// <summary>
    /// Pin (owner, 22/10/2026 18:41 AEDT; mutation-checked): the Advanced Speech model card's introduction and the Simple view's
    /// explainer show the owner's replacement sentence, byte for byte, only while cloud speech sends audio, and the approved
    /// originals byte for byte once it is off.
    /// </summary>
    [Theory]
    [InlineData(UiSettings.Advanced)]
    [InlineData(UiSettings.Simple)]
    public Task The_models_page_on_this_PC_sentences_follow_cloud_speech(string view) => UiSession.Run(() =>
    {
        using var scratch = new Scratch($$"""{ "hotkey": "F24", "ui": { "view": "{{view}}" }{{SpeechAt()}} }""");
        SpeechConsent(scratch);
        var (shell, _, _) = Shell(scratch);
        var (approved, replaced) = view == UiSettings.Advanced ? (ApprovedSpeechIntro, ReplacedSpeechIntro) : (ApprovedExplainer, ReplacedExplainer);
        try
        {
            shell.Reload(userRequested: false);
            Assert.True(shell.SpeechSendsAudio);
            var on = PageLines(shell, "Models");
            Assert.Equal(Encoding.UTF8.GetBytes(replaced), Encoding.UTF8.GetBytes(on.Single(l => l == replaced)));
            Assert.DoesNotContain(approved, on);
            Assert.Null(shell.DisableCloudSpeech());
            var off = PageLines(shell, "Models");
            Assert.Equal(Encoding.UTF8.GetBytes(approved), Encoding.UTF8.GetBytes(off.Single(l => l == approved)));
            Assert.DoesNotContain(replaced, off);
        }
        finally
        {
            shell.Dispose();
        }
    });

    /// <summary>Fails any request: these pins only draw pages.</summary>
    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no request is expected");
    }
}
