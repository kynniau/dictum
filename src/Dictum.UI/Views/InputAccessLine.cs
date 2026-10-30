using Avalonia.Automation;
using Avalonia.Controls;
using Dictum.Platform;
using Dictum.UI.Shell;

namespace Dictum.UI.Views;

/// <summary>
/// The words of the input-access surfaces (docs/DECISIONS.md "Request access", 27/10/2026). Every string here is APPROVED by the owner 27/10/2026 19:00 AEDT
/// (Mac request-access block, rows 1-6, .demo-preview/approve-mac-access.md): do not reword. They ship only on a platform whose check is not NotApplicable, so no
/// Windows surface changes. The device word comes from the approved Mac copy block (<see cref="PlatformWords.ThisDevice"/>).
/// </summary>
internal static class InputAccessText
{
    /// <summary>APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 3): do not reword. The button, the owner's words ("request access").</summary>
    public const string RequestLabel = "Request access";

    /// <summary>
    /// APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 1): do not reword. The status line while the grant is missing. It names no Settings page: macOS 15 and 26 call it
    /// "Accessibility" and macOS 27 "Device Control and Data Access" (the owner's screenshot, 27/10/2026), and the button opens it.
    /// </summary>
    public static string Denied(PlatformWords words) =>
        $"Dictum is not allowed to control {words.ThisDevice} yet, so it cannot see the hotkey or paste. Press Request access, then turn Dictum on in the System Settings page that opens.";

    /// <summary>APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 2): do not reword. The caption under the status line, for the stale-entry case the owner hit (seven entries, one switch shown on).</summary>
    public const string StaleEntryNote = "If Dictum is already listed there and on but still refused, remove it from that list with the − button, then press Request access again.";

    /// <summary>APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 4): do not reword. The status line once the grant arrived and the listener installed (shown until the page is next redrawn).</summary>
    public const string Granted = "Dictum can now see the hotkey.";

    /// <summary>APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 6): do not reword. The grant arrived but the listener still did not install.</summary>
    public const string RestartFailed = "Access was granted, but the hotkey still could not be set up. Quit and reopen Dictum.";

    /// <summary>APPROVED by the owner 27/10/2026 19:00 AEDT (Mac request-access block, row 5): do not reword. The hotkey capture box's line in place of "The system refused to report keys…" when the grant is what is missing.</summary>
    public static string BoxDenied(PlatformWords words) => $"Dictum cannot listen for keys until it is allowed to control {words.ThisDevice}.";
}

/// <summary>
/// The one input-access surface, shown wherever the hotkey cannot work for want of the grant: the state in words, the stale-entry
/// caption, and the Request access button (SPEC §21). Hidden while the state is NotApplicable (Windows, always) or Granted with
/// nothing to say, so the pages are laid out as before. The status is a polite live region; the button carries its own name.
/// </summary>
internal sealed class InputAccessLine : StackPanel
{
    private readonly AppShell _shell;
    private readonly TextBlock _status = Ui.Text("", "body");
    private readonly TextBlock _note = Ui.Text(InputAccessText.StaleEntryNote, "caption");
    private readonly Button _request;
    private readonly bool _showsItself;
    private bool _granted;
    private InputAccessState _seen;

    /// <param name="showsItself">True: shown whenever the state calls for it (a page). False: its owner shows and hides it (the capture box, after its own refusal).</param>
    public InputAccessLine(AppShell shell, bool showsItself = true)
    {
        _shell = shell;
        _showsItself = showsItself;
        _request = Ui.Button(InputAccessText.RequestLabel, "primary", () => _shell.Access.Request(), name: InputAccessText.RequestLabel);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Spacing = 8;
        Children.Add(_status);
        Children.Add(_note);
        Children.Add(Ui.Row(8, _request));
        AttachedToVisualTree += (_, _) =>
        {
            // A fresh view: "Dictum can now see the hotkey." is said only for a grant that arrives while this line is on screen,
            // never for one that was already in place when the page opened (merge lens, PR #82, 27/10/2026).
            _granted = false;
            _seen = _shell.Access.State;
            _shell.Access.Changed += AccessChanged;
            Render();
        };
        DetachedFromVisualTree += (_, _) => _shell.Access.Changed -= AccessChanged;
        _seen = _shell.Access.State;
        Render();
    }

    /// <summary>The line as it reads now, for the pins: null while hidden.</summary>
    internal string? StatusText => IsVisible ? _status.Text : null;

    /// <summary>The grant changed while this line is attached: a Denied-to-Granted change seen here is the one row 4 announces.</summary>
    private void AccessChanged()
    {
        var state = _shell.Access.State;
        _granted = state == InputAccessState.Granted && (_granted || _seen == InputAccessState.Denied);
        _seen = state;
        Render();
    }

    private void Render()
    {
        var access = _shell.Access;
        var denied = access.State == InputAccessState.Denied;
        if (_showsItself) IsVisible = denied || access.RestartFailed || (_granted && access.State == InputAccessState.Granted);
        _status.Text = denied ? InputAccessText.Denied(_shell.Platform.Words)
            : access.RestartFailed ? InputAccessText.RestartFailed
            : InputAccessText.Granted;
        _note.IsVisible = denied;
        _request.IsVisible = denied;
    }
}
