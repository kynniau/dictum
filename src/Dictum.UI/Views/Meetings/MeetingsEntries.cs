using Avalonia.Controls;
using Dictum.Meetings;

namespace Dictum.UI.Views.Meetings;

internal sealed class MeetingsEntries : StackPanel
{
    private readonly Func<MeetingsSettings> _settings;
    private readonly Func<bool> _available;
    /// <param name="available">False hides both entries whatever meetings.enabled says (AppShell.MeetingsAvailable); the window's own copy passes none.</param>
    public MeetingsEntries(Func<MeetingsSettings> settings, Action start, Action list, Func<bool>? available = null)
    {
        _settings = settings;
        _available = available ?? (() => true);
        Spacing = 8;
        Children.Add(MeetingsWindow.Button(MeetingsText.PROPOSED_Start, start));
        Children.Add(MeetingsWindow.Button(MeetingsText.PROPOSED_Meetings, list));
        Refresh();
    }
    public void Refresh() => IsVisible = _available() && _settings().Enabled;
}
