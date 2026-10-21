using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.Personal;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>
/// Settings → Personal (SPEC §16.7, 21/10/2026): the About you card, a sentence or two about the speaker kept in context.txt and
/// given to the clean-up model for its choice of spellings and terms. The box follows the file (an edit in the file, a reload)
/// until the user types in it; Save writes it, Clear deletes the file.
/// </summary>
internal sealed class PersonalPage : SettingsPage
{
    /// <summary>The card's title. PROPOSED 21/10/2026, awaiting the product owner's approval.</summary>
    public const string CardTitle = "About you";

    /// <summary>The example, also the empty box's placeholder. PROPOSED 21/10/2026.</summary>
    public const string Example = "I work for an Australian finance company: Australian lenders, bank names and mortgage terms.";

    /// <summary>The card's help text. PROPOSED 21/10/2026.</summary>
    public const string Help = "Tell the clean-up model who you are and what you talk about, so it picks the right spellings and terms. Example: " +
                               Example + " This never adds words you did not say.";

    /// <summary>
    /// Where the text goes when cloud clean-up is on. PROPOSED 21/10/2026 for the Clean-up page's cloud section, whose file
    /// another lane owns; shown here, under the box, until the product owner places it.
    /// </summary>
    public const string CloudNote = "Your About you text is sent along with your dictation when cloud clean-up is on.";

    public const string Saved = "Saved. The clean-up model uses it from the next dictation.";
    public const string Cleared = "Cleared. The clean-up model gets no text about you.";

    private readonly TextBox _box = new()
    {
        Watermark = Example,
        AcceptsReturn = false, // one paragraph: a line break in the file is read as a space
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 160,
    };
    private readonly TextBlock _count = Ui.Text("", "caption");
    private string _saved = "";

    public PersonalPage(AppShell shell) : base(shell, "Personal")
    {
        AutomationProperties.SetName(_box, CardTitle);
        _box.TextChanged += (_, _) => Count();
        var save = Ui.Button("Save", "primary", () => Run(() => Save(_box.Text ?? "")));
        var clear = Ui.Button("Clear", "secondary", () => Run(() => Save("")));
        Compose(Ui.Card(Ui.Stack(10,
            Ui.Text(Help),
            _box,
            _count,
            Ui.Row(8, save, clear),
            Ui.Text(CloudNote, "caption")), CardTitle));
    }

    private void Save(string text)
    {
        if (Shell.Personal.SaveContext(text) is { } problem)
        {
            Say(problem);
            return;
        }
        _saved = SpeakerContext.Normalise(text);
        _box.Text = _saved;
        Say(_saved.Length == 0 ? Cleared : Saved);
    }

    private void Count()
    {
        var length = SpeakerContext.Normalise(_box.Text).Length;
        _count.Text = length > SpeakerContext.MaxChars
            ? $"{length} characters: {length - SpeakerContext.MaxChars} over the limit of {SpeakerContext.MaxChars}."
            : $"{length} of {SpeakerContext.MaxChars} characters.";
    }

    protected override void Redraw()
    {
        var inFile = Shell.Personal.ContextForEditing();
        if ((_box.Text ?? "") == _saved) _box.Text = inFile; // no unsaved typing: follow the file
        _saved = inFile;
        Count();
    }
}
