using Avalonia.Automation;
using Avalonia.Controls;
using Dictum.Help;
using Dictum.Platform;
using Dictum.Settings;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// "Add a key…" under a cloud service (API keys, brief §2 item 1, decisions D1 and D2; K-UI lane 04/11/2026): a Label, prefilled with
/// the service's name, and a masked Key field to paste into; Save hands both to <see cref="ISecretVault.Add"/>, which keeps the key in
/// the system's credential store. A refusal is shown as the dialog's error line, in the vault's own words (approve-api-keys.md row 14).
/// The key is never shown, never logged and never kept: the field is masked, has no undo history to bring it back, is emptied as soon
/// as Add returns (refused or not) and when the window closes, and nothing here holds it after the click. Opened on purpose, so it
/// takes focus; built on the shared dialog frame (<see cref="DialogWindow"/>), like Add a word.
/// </summary>
internal sealed class AddKeyWindow : DialogWindow
{
    // PROPOSED 04/11/2026 (approve-api-keys.md rows 17 to 22): the dialog's own words.

    /// <summary>PROPOSED 04/11/2026 (row 17): the dialog's heading.</summary>
    internal const string HeadingText = "Add a key";

    /// <summary>PROPOSED 04/11/2026 (row 18): the line under the heading.</summary>
    internal static string Intro(string service, string store) =>
        $"For {service}. Paste the API key you created on its website. Dictum keeps it in {store}, never writes it to a file, and never shows it again.";

    /// <summary>PROPOSED 04/11/2026 (row 19): the Label field's caption and accessible name.</summary>
    internal const string LabelCaption = "Label";

    /// <summary>PROPOSED 04/11/2026 (row 19): the hint under the Label field.</summary>
    internal const string LabelHint = "A name for this key, such as Work or Personal. It is shown in the list; the key never is.";

    /// <summary>PROPOSED 04/11/2026 (row 20): the Key field's caption.</summary>
    internal const string KeyCaption = "Key";

    /// <summary>PROPOSED 04/11/2026 (row 20): the Key field's accessible name: a screen reader also says it is a password field.</summary>
    internal const string KeyName = "API key, hidden as you paste";

    /// <summary>PROPOSED 04/11/2026 (row 20): the Key field's placeholder.</summary>
    internal const string KeyWatermark = "Paste your key here";

    /// <summary>PROPOSED 04/11/2026 (row 21).</summary>
    internal const string SaveLabel = "Save";

    /// <summary>PROPOSED 04/11/2026 (row 21).</summary>
    internal const string CancelLabel = "Cancel";

    /// <summary>PROPOSED 04/11/2026 (row 22): the link to the site help page's section for this service.</summary>
    internal const string HowLabel = "How do I get a key?";

    /// <summary>
    /// The site help page section for each key variable (row 22; K-Docs creates the sections with these ids). Anthropic, OpenAI and
    /// Google Gemini are clean-up services, Azure OpenAI and Groq speech services; both custom servers share one section.
    /// </summary>
    internal static string GuideAnchor(string variable) => variable switch
    {
        SecretStore.AnthropicKey => "#key-anthropic",
        SecretStore.OpenAiKey => "#key-openai",
        SecretStore.GeminiKey => "#key-gemini",
        SecretStore.AzureOpenAiKey => "#key-azure",
        SecretStore.GroqKey => "#key-groq",
        _ => "#key-custom", // LLM_API_KEY and SPEECH_API_KEY: a server of your own
    };

    /// <summary>The page the link opens: the site help page at the service's section.</summary>
    internal static string GuideLink(string variable) => HelpText.GuideUrl + GuideAnchor(variable);

    /// <summary>
    /// Every refusal <see cref="ISecretVault.Add"/> raises for what was typed (approve-api-keys.md row 14), shown byte for byte. An
    /// <see cref="ArgumentException"/>'s Message carries " (Parameter 'label')" after it, so the known sentence is found at its start.
    /// </summary>
    internal static readonly IReadOnlyList<string> Refusals =
    [
        "The label is empty.",
        "The key is empty.",
        "The key holds a line break or another control character.",
        $"The key is longer than {SecretVault.ValueMaxBytes} bytes.",
        "The label holds part of the key.",
    ];

    private readonly ISecretVault _vault;
    private readonly string _variable;
    private readonly TextBox _label;
    private readonly TextBox _key;
    private readonly TextBlock _error = Ui.Text("", "caption");

    /// <param name="vault">Where the key goes (the shell's <see cref="Shell.AppShell.Vault"/>; a test passes a fake).</param>
    /// <param name="variable">The key variable of the service the dialog was opened under.</param>
    /// <param name="service">The service's name as the page shows it, the Label's starting text.</param>
    /// <param name="open">Opens the help link (the platform's <see cref="IPlatform.Open"/>).</param>
    public AddKeyWindow(ISecretVault vault, string variable, string service, Action<string> open) : base(HeadingText, 500)
    {
        _vault = vault;
        _variable = variable;
        _label = new TextBox { Text = service, MaxLength = StoredKey.LabelMaxLength };
        AutomationProperties.SetName(_label, LabelCaption);
        // Masked, and no undo history: once emptied, nothing (Ctrl+Z included) brings the key back. A password box also refuses copy and cut.
        _key = new TextBox { PasswordChar = '•', RevealPassword = false, IsUndoEnabled = false, Watermark = KeyWatermark, AcceptsReturn = false };
        AutomationProperties.SetName(_key, KeyName);
        _error.IsVisible = false;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var save = Ui.Button(SaveLabel, "primary", Save);
        save.IsDefault = true;
        var cancel = Ui.Button(CancelLabel, "secondary", Close);
        var how = Ui.Button(HowLabel, "link", () => open(GuideLink(variable)));
        Compose(Ui.Stack(10,
            Ui.Text(Intro(service, vault.StoreName ?? "")),
            Ui.Stack(4, Ui.Text(LabelCaption, "caption"), _label, Ui.Text(LabelHint, "hint")),
            Ui.Stack(4, Ui.Text(KeyCaption, "caption"), _key),
            _error,
            how), cancel, save);
        Opened += (_, _) => _key.Focus();
        Closed += (_, _) => _key.Text = ""; // cleared on close, whichever way it closed
    }

    /// <summary>The new key's id once Save stored it; null while nothing was stored (cancelled, or refused).</summary>
    public string? AddedId { get; private set; }

    /// <summary>The dialog's error line, empty when there is none.</summary>
    internal string Error => _error.Text ?? "";

    /// <summary>What the Key field holds now (tests: empty after every Save and after closing).</summary>
    internal string KeyText => _key.Text ?? "";

    internal TextBox LabelBox => _label;

    internal TextBox KeyBox => _key;

    private void Save()
    {
        string? error = null;
        try
        {
            AddedId = _vault.Add(_variable, _label.Text ?? "", _key.Text ?? "");
        }
        catch (ArgumentException ex)
        {
            error = Refusals.FirstOrDefault(r => ex.Message.StartsWith(r, StringComparison.Ordinal)) ?? "The key was not saved."; // the vault names no value
        }
        catch (CredentialStoreException ex)
        {
            error = ex.Message; // row 14's "This platform has no credential store Dictum can use.", or the store's call and code: never a value
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message; // keys.json could not be written (row 13's sentence when it cannot be read): a path at most, never a value
        }
        finally
        {
            _key.Text = ""; // Add has returned: nothing of the key stays here, refused or not
        }
        if (error is null)
        {
            Close();
            return;
        }
        _error.Text = error;
        _error.IsVisible = true;
        _key.Focus();
    }
}
