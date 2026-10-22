using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings > Clean-up, Advanced view only (1.3): a box for the user's own clean-up instructions, saved as llm.instructions when
/// the box loses focus or leaves the window. They go into the prompt of the local model and of cloud clean-up
/// (<see cref="CleanupPrompt.Build"/>), never into the rule-based tidy or off, and every reply is still checked by the validator.
/// The text is shown here only: the log gets its length, and no email, state line or demonstration shows it.
/// </summary>
internal sealed class OwnInstructionsCard
{
    // PROPOSED 22/10/2026 (1.3): the card's words.
    internal const string Label = "Your own instructions";
    internal const string Hint = "Added to Dictum's clean-up instructions for the AI and cloud clean-up. Dictum still checks every reply against what you said.";

    /// <summary>PROPOSED 22/10/2026 (1.3): shown while the box holds the most Dictum uses (<see cref="LlmSettings.InstructionsMaxLength"/>).</summary>
    internal const string LimitLine = "That is the limit: Dictum uses up to 2,000 characters of your own instructions.";

    /// <summary>PROPOSED 22/10/2026 (1.3): under the box while cloud clean-up is on (a host agreed to in the consent step).</summary>
    internal static string CloudLine(string host) => $"With cloud clean-up on, these instructions go to {host} with each dictation.";

    /// <summary>PROPOSED 22/10/2026 (1.3, orchestrator decision after the lens on 5e9c227): the Simple view hides the box, so it says when instructions apply.</summary>
    internal const string SimpleLine = "Your own clean-up instructions are in use (Advanced view).";

    private readonly AppShell _shell;
    private readonly Action<string> _say;
    private readonly TextBox _box = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxLength = LlmSettings.InstructionsMaxLength,
        MinHeight = 96,
        MaxHeight = 240,
    };
    private readonly TextBlock _limit = Ui.Text(LimitLine, "caption");
    private readonly TextBlock _cloud = Ui.Text("", "caption");
    private readonly TextBlock _simpleNote = Ui.Text(SimpleLine, "caption");
    private bool _drawing;

    /// <param name="say">The page's status line, for a save that failed.</param>
    public OwnInstructionsCard(AppShell shell, Action<string> say)
    {
        _shell = shell;
        _say = say;
        AutomationProperties.SetName(_box, Label);
        AutomationProperties.SetHelpText(_box, Hint);
        AutomationProperties.SetLiveSetting(_limit, AutomationLiveSetting.Polite); // said when it appears, as the calibration window's lines are
        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ShowLimit(); // on the property itself: a TextChanged handler did not update the line in the headless test
        };
        _box.LostFocus += (_, _) => Save();
        _box.DetachedFromVisualTree += (_, _) => Save(); // the page or the window closed with the box focused
        Card = Ui.Card(Ui.Stack(8, _box, Ui.Text(Hint, "hint"), _limit, _cloud), Label);
        View = Ui.Stack(18, Card, _simpleNote);
        _simpleNote.IsVisible = false;
        ShowLimit();
    }

    /// <summary>What the page places: the card (Advanced view) and the Simple view's line.</summary>
    public StackPanel View { get; }

    /// <summary>The card itself.</summary>
    public Border Card { get; }

    /// <summary>The box (for the page's tests).</summary>
    internal TextBox Box => _box;

    /// <summary>
    /// From the settings last loaded: the card in the Advanced view only, and in the Simple view one line while instructions are set
    /// (so none apply unseen); the box is not rewritten while it is being typed in.
    /// </summary>
    public void Redraw(AppSettings s)
    {
        _drawing = true;
        try
        {
            Card.IsVisible = !s.Ui.IsSimple;
            _simpleNote.IsVisible = s.Ui.IsSimple && s.Llm.Instructions is { Length: > 0 };
            View.IsVisible = Card.IsVisible || _simpleNote.IsVisible; // no empty gap on the page
            if (!_box.IsFocused) _box.Text = s.Llm.Instructions ?? "";
            var host = CloudPolicy.CloudHost(s.Llm);
            _cloud.Text = host is not null && CloudPolicy.HasConsent(s.Llm) ? CloudLine(host) : "";
            _cloud.IsVisible = _cloud.Text.Length > 0;
            ShowLimit();
        }
        finally
        {
            _drawing = false;
        }
    }

    private void ShowLimit() => _limit.IsVisible = (_box.Text?.Length ?? 0) >= LlmSettings.InstructionsMaxLength;

    /// <summary>Writes llm.instructions when the text differs from the settings in force (empty: null); a failure is said, never thrown.</summary>
    internal void Save()
    {
        if (_drawing) return;
        var text = (_box.Text ?? "").Trim();
        if (text == (_shell.Settings.Llm.Instructions ?? "")) return;
        try
        {
            _shell.Write(("llm.instructions", text.Length == 0 ? null : System.Text.Json.Nodes.JsonValue.Create(text)));
        }
        catch (Exception ex)
        {
            _shell.Log.Error($"Settings: {ex.GetType().Name} saving llm.instructions.");
            _say($"That did not work: {ex.Message}");
        }
    }
}

/// <summary>
/// Settings > Clean-up, beside "Open the settings folder" on the cloud card (1.3): "Create the key file…" writes .env with the chosen
/// service's variable and the placeholder, or opens the .env already there unchanged (<see cref="AppShell.CreateKeyFile"/>).
/// </summary>
internal static class KeyFileButton
{
    /// <summary>PROPOSED 22/10/2026 (1.3).</summary>
    internal const string Label = "Create the key file…";

    /// <summary>
    /// The variable the cloud card's key line names for the service chosen and the address typed (null for Anthropic's own entry,
    /// which is not a preset): Anthropic's, or the one <see cref="CloudPresets.ForAddress"/> gives. Null: that service takes no key.
    /// </summary>
    internal static string? VariableFor(CloudPreset? preset, string? address) =>
        preset is null ? SecretStore.AnthropicKey : CloudPresets.ForAddress(address ?? preset.BaseUrl).KeyVariable;

    /// <param name="variable">The variable for the service the card shows now (<see cref="VariableFor"/>), read at each click.</param>
    /// <param name="noKey">What the card says for a service that takes no key (its own key line), read at the click.</param>
    /// <param name="say">The page's status line.</param>
    public static Button Create(AppShell shell, Func<string?> variable, Func<string> noKey, Action<string> say) =>
        Ui.Button(Label, "secondary", () =>
        {
            try
            {
                say(variable() is { } name ? shell.CreateKeyFile(name) : noKey());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shell.Log.Error($"Settings: the key file could not be created: {ex.GetType().Name}.");
                say($"That did not work: {ex.Message}");
            }
        });
}
