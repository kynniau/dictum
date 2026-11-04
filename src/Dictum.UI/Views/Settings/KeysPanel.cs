using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.UI.Shell;

namespace Dictum.UI.Views.Settings;

/// <summary>Where a key in a service's list lives.</summary>
internal enum KeySource
{
    /// <summary>Added in Dictum: in the system's credential store, named in keys.json.</summary>
    Stored,

    /// <summary>A real environment variable of the service's name.</summary>
    Environment,

    /// <summary>A line in a .env file (the config folder's, else the one beside the program).</summary>
    File,
}

/// <summary>One key in a service's list: never its value (decision D2), only where it lives and its label.</summary>
/// <param name="Id">The stored key's id; null for the environment variable and the .env line.</param>
/// <param name="Label">The user's label (stored keys; empty when the vault blanked it), else the variable's name.</param>
internal sealed record KeyEntry(KeySource Source, string? Id, string Label, DateOnly? Added)
{
    /// <summary>What keys.active holds for this entry: the id, "env" or "file".</summary>
    public string Choice => Source switch
    {
        KeySource.Environment => StoredKeyId.Environment,
        KeySource.File => StoredKeyId.File,
        _ => Id!,
    };
}

/// <summary>A service's keys as the list shows them, and why the stored ones could not be read, if they could not.</summary>
internal sealed record KeyListing(string Variable, IReadOnlyList<KeyEntry> Entries, string? Problem)
{
    /// <summary>
    /// Every key for <paramref name="variable"/>: the stored ones in keys.json's order (<see cref="ISecretVault.List"/>), then the
    /// environment variable and the .env line when there is one. A store that cannot be read (<see cref="CredentialStoreException"/>)
    /// gives no stored entries and the reason, and the rest of the list still shows.
    /// </summary>
    public static KeyListing Read(ISecretVault vault, AppPaths paths, string variable, Func<string, string?> environment)
    {
        var entries = new List<KeyEntry>();
        string? problem = null;
        if (vault.Available)
        {
            try
            {
                entries.AddRange(vault.List(variable).Select(k => new KeyEntry(KeySource.Stored, k.Id, k.Label, k.Added)));
            }
            catch (CredentialStoreException ex)
            {
                problem = KeysPanel.CouldNotRead(vault.StoreName ?? "the credential store", ex.Message);
            }
        }
        if (environment(variable) is { Length: > 0 }) entries.Add(new KeyEntry(KeySource.Environment, null, variable, null));
        if (AppShell.FileKey(paths, variable) is not null) entries.Add(new KeyEntry(KeySource.File, null, variable, null));
        return new KeyListing(variable, entries, problem);
    }

    /// <summary>Stands in for each source's value when <see cref="Active"/> asks SecretStore: never a key, only "this source has one".</summary>
    private const string StandIn = "set";

    /// <summary>
    /// The entry the next dictation uses, decided by <see cref="SecretStore.InUse"/> itself (SPEC §5; lens on PR #87, MINOR 9: one order,
    /// not a copy of it): a store holding a stand-in value for each source this listing found, and <paramref name="choice"/>. Null: none
    /// is used (two or more stored, none chosen, nothing else). Pinned against SecretStore.Describe (ApiKeysUiTests).
    /// </summary>
    public KeyEntry? Active(string? choice)
    {
        var secrets = SecretStore.FromContents(
            Entries.Any(e => e.Source == KeySource.File) ? $"{Variable}={StandIn}\n" : null, null,
            v => v == Variable && Entries.Any(e => e.Source == KeySource.Environment) ? StandIn : null,
            choice is null ? null : new Dictionary<string, string> { [Variable] = choice },
            Entries.Where(e => e.Source == KeySource.Stored).Select(e => (new StoredKey(e.Id!, Variable, e.Label, e.Added), StandIn)).ToList());
        return secrets.InUse(Variable) is { } used ? Entries.FirstOrDefault(e => e.Choice == used) : null;
    }
}

/// <summary>
/// The key list under a cloud service (API keys, brief §2 items 1, 2 and 5; K-UI lane 04/11/2026): each key's label, where it lives,
/// an Active mark or "Use this key", Check this key (only once the consent step for that service's host is on record, D3), and Remove
/// for a key added here; then "Add a key…", or, where the platform has no credential store, a line saying where keys go instead.
/// Settings > Clean-up and Settings > Models share it. It is rebuilt only when the settings load or the service shown changes, or after
/// its own action, never on a download's progress ticks: listing reads the credential store.
/// </summary>
internal sealed class KeysPanel
{
    // PROPOSED 04/11/2026 (approve-api-keys.md rows 16 and 23 to 29): the list's own words.

    /// <summary>PROPOSED 04/11/2026 (row 16): the button that opens the dialog.</summary>
    internal const string AddLabel = "Add a key…";

    /// <summary>PROPOSED 04/11/2026 (row 23): in place of the button where there is no credential store Dictum can use (macOS until the K-Mac lane).</summary>
    internal const string NoStoreLine = "Adding a key here needs a credential store Dictum can use, which this computer does not have yet. Put the key in the key file instead.";

    /// <summary>PROPOSED 04/11/2026 (row 24): where each kind of key lives.</summary>
    internal static string Where(KeyEntry entry) => entry.Source switch
    {
        KeySource.Environment => "Environment variable",
        KeySource.File => "Key file (.env)",
        _ => entry.Added is { } day ? $"Added here on {day:dd'/'MM'/'yyyy}" : "Added here",
    };

    /// <summary>PROPOSED 04/11/2026 (row 25): the mark on the key in use, and the button on the others.</summary>
    internal const string ActiveLabel = "Active";

    /// <summary>PROPOSED 04/11/2026 (row 25).</summary>
    internal const string UseLabel = "Use this key";

    /// <summary>PROPOSED 04/11/2026 (row 26).</summary>
    internal const string RemoveLabel = "Remove";

    /// <summary>PROPOSED 04/11/2026 (row 37).</summary>
    internal const string CheckLabel = "Check this key";

    /// <summary>PROPOSED 04/11/2026 (row 25): the buttons' accessible names, which say which key each acts on.</summary>
    internal static string UseName(string label) => $"{UseLabel}: {label}"; // the visible words first (WCAG 2.5.3, as CalibrationWindow names its rows)

    internal static string CheckName(string label) => $"{CheckLabel}: {label}";

    internal static string RemoveName(string label) => $"Remove {label}";

    /// <summary>PROPOSED 04/11/2026 (row 26): Remove's question.</summary>
    internal const string RemoveTitle = "Remove this key?";

    /// <summary>PROPOSED 04/11/2026 (row 26).</summary>
    internal static string RemoveText(string label, string store) =>
        $"Dictum removes the key \"{label}\" from {store}. There is no undo: to use it again, add it again.";

    /// <summary>PROPOSED 04/11/2026 (row 27): after Save, the key added (Dictum also makes it the one in use).</summary>
    internal static string Added(string label) => $"Key \"{label}\" added. Dictum uses it from the next dictation.";

    /// <summary>PROPOSED 04/11/2026 (row 27): saved, but settings.json could not record it as the one in use.</summary>
    internal static string AddedNotChosen(string label, string failure) =>
        $"Key \"{label}\" added, but it could not be made the one in use ({failure}). Choose Use this key on it once settings.json can be saved.";

    /// <summary>PROPOSED 04/11/2026 (row 27).</summary>
    internal const string Removed = "Key removed.";

    /// <summary>PROPOSED 04/11/2026 (row 27).</summary>
    internal static string NowUsing(string label) => $"Dictum now uses \"{label}\" from the next dictation.";

    /// <summary>PROPOSED 04/11/2026 (row 28): the stored keys could not be read; the rest of the list and the page still work.</summary>
    internal static string CouldNotRead(string store, string reason) =>
        $"Your stored keys could not be read from {store} ({reason.TrimEnd('.')}). Keys in the key file and environment variables still work.";

    /// <summary>PROPOSED 04/11/2026 (row 29): two or more stored, none chosen, nothing else: none is sent.</summary>
    internal const string NoneChosen = "Two or more keys are stored and none is chosen, so none is used: choose Use this key on one.";

    /// <summary>PROPOSED 04/11/2026 (row 29): no key yet.</summary>
    internal const string NoKeyYet = "No key yet.";

    /// <summary>PROPOSED 04/11/2026 (row 29): a stored key whose label the vault left out (empty, or it held part of the key).</summary>
    internal const string Unnamed = "Unnamed key";

    /// <summary>PROPOSED 04/11/2026 (row 37): while a check is under way.</summary>
    internal static string Checking(string label) => $"Checking \"{label}\"…";

    private readonly AppShell _shell;
    private readonly ISecretVault _vault;
    private readonly Func<string, string?> _environment;
    private readonly Action<string> _say;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _note = Ui.Text("", "caption");
    private readonly TextBlock _noStore = Ui.Text(NoStoreLine, "caption");
    private readonly Button _add;
    private (string? Variable, AppSettings? Settings) _drawn;
    private string? _variable;
    private string _service = "";

    /// <param name="vault">The keys added in Dictum (the shell's <see cref="AppShell.Vault"/>; a test passes a fake).</param>
    /// <param name="say">The page's status line.</param>
    /// <param name="environment">Reads an environment variable (the process's own; a test passes its own).</param>
    public KeysPanel(AppShell shell, ISecretVault vault, Action<string> say, Func<string, string?>? environment = null)
    {
        _shell = shell;
        _vault = vault;
        _say = say;
        _environment = environment ?? System.Environment.GetEnvironmentVariable;
        _add = Ui.Button(AddLabel, "secondary", OpenAdd, Failed); // a failure after the dialog is said on the page, never unhandled
        _note.IsVisible = false;
        View = Ui.Stack(8, _rows, _note, Ui.Row(8, _add), _noStore);
    }

    /// <summary>The panel, placed under the service's fields on its card.</summary>
    public StackPanel View { get; }

    /// <summary>The rows drawn last (tests read them).</summary>
    internal KeyListing? Listing { get; private set; }

    /// <summary>
    /// Shows the keys for <paramref name="variable"/> (null: the service takes no key, and the panel is hidden), called by the page's
    /// redraw. The list is read again only when the settings were loaded again, the service changed, or <paramref name="force"/>.
    /// </summary>
    public void Show(string? variable, string service, bool force = false)
    {
        View.IsVisible = variable is not null;
        _service = service;
        if (variable is null) return;
        if (!force && _drawn.Variable == variable && ReferenceEquals(_drawn.Settings, _shell.Settings)) return;
        _variable = variable;
        _drawn = (variable, _shell.Settings);
        Draw(variable);
    }

    private void Draw(string variable)
    {
        var listing = KeyListing.Read(_vault, _shell.Paths, variable, _environment);
        Listing = listing;
        _shell.Settings.Keys.Active.TryGetValue(variable, out var choice);
        var active = listing.Active(choice);
        var check = KeyCheck.Target(variable, _shell.Settings) is not null;
        _rows.Children.Clear();
        foreach (var entry in listing.Entries) _rows.Children.Add(Row(variable, entry, entry == active, check));
        var stored = listing.Entries.Count(e => e.Source == KeySource.Stored);
        _note.Text = listing.Problem
                     ?? (active is null && stored > 1 ? NoneChosen : null)
                     ?? (listing.Entries.Count == 0 ? NoKeyYet : "");
        _note.IsVisible = _note.Text.Length > 0;
        _add.IsVisible = _vault.Available;
        _noStore.IsVisible = !_vault.Available;
    }

    private static string Label(KeyEntry entry) => entry.Label.Length > 0 ? entry.Label : Unnamed;

    private Grid Row(string variable, KeyEntry entry, bool active, bool check)
    {
        var label = Label(entry);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Ui.Stack(2, Ui.Text(label, "body"), Ui.Text(Where(entry), "hint")));
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, ItemsAlignment = WrapPanelItemsAlignment.End, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(active ? Ui.Tag(ActiveLabel) : Ui.Button(UseLabel, "secondary", () => Choose(variable, entry), Failed, UseName(label)));
        if (check)
        {
            Button? checking = null;
            checking = Ui.Button(CheckLabel, "secondary", () => _ = CheckAsync(variable, entry, checking!), Failed, CheckName(label));
            actions.Children.Add(checking);
        }
        if (entry.Source == KeySource.Stored) actions.Children.Add(Ui.Button(RemoveLabel, "secondary", () => Remove(entry), Failed, RemoveName(label)));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private void Failed(Exception ex)
    {
        _shell.Log.Error($"Settings: keys: {ex.GetType().Name}.");
        _say($"That did not work: {ex.Message}");
    }

    private void Choose(string variable, KeyEntry entry)
    {
        _say(_shell.ChooseKey(variable, entry.Choice) is { } failure ? $"That did not work: {failure}" : NowUsing(Label(entry)));
        Show(variable, _service, force: true);
    }

    /// <summary>One request per click; the button is off while it runs, so a second click cannot send another.</summary>
    internal async Task CheckAsync(string variable, KeyEntry entry, Button button)
    {
        button.IsEnabled = false;
        _say(Checking(Label(entry)));
        try
        {
            _say(await _shell.CheckKeyAsync(variable, entry.Choice));
        }
        catch (Exception ex)
        {
            Failed(ex); // a store that cannot be read: its call and code, never a value
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Remove(KeyEntry entry)
    {
        var store = _vault.StoreName ?? "the credential store";
        if (_shell.Dialogs.Ask(RemoveTitle, RemoveText(Label(entry), store), new DialogChoice(RemoveLabel, IsDanger: true), new DialogChoice("Cancel", IsDefault: true)) != 0) return;
        try
        {
            _say(_vault.Remove(entry.Id!) ? Removed : AppShell.KeyGone);
        }
        catch (Exception ex) when (ex is CredentialStoreException or IOException or UnauthorizedAccessException)
        {
            _say($"That did not work: {ex.Message}");
        }
        if (_variable is { } variable) Show(variable, _service, force: true);
    }

    private void OpenAdd()
    {
        if (_variable is not { } variable) return;
        var window = new AddKeyWindow(_vault, variable, _service, url => _shell.Platform.Open(url));
        Modal.Run(window);
        Added(variable, window.AddedId);
    }

    /// <summary>
    /// After the dialog: a key saved is made the one in use (the user just pasted it to use it; an older .env line or environment
    /// variable would otherwise win), and the list is drawn again. Nothing when it was cancelled or refused.
    /// </summary>
    internal void Added(string variable, string? id)
    {
        if (id is null) return;
        string label;
        try
        {
            label = _vault.List(variable).FirstOrDefault(k => k.Id == id) is { Label.Length: > 0 } key ? key.Label : Unnamed;
        }
        catch (CredentialStoreException)
        {
            label = Unnamed; // the key was stored; only its label could not be read back (the list then says why)
        }
        _say(_shell.ChooseKey(variable, id) is { } failure ? AddedNotChosen(label, failure) : Added(label));
        Show(variable, _service, force: true);
    }
}
