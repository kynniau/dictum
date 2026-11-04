using Avalonia.Controls;
using Dictum.Meetings;

namespace Dictum.UI.Views.Meetings;

internal sealed class MeetingsEntries : StackPanel
{
    private readonly Func<MeetingsSettings> _settings;
    public MeetingsEntries(Func<MeetingsSettings> settings, Action start, Action list)
    {
        _settings = settings;
        Spacing = 8;
        Children.Add(MeetingsWindow.Button(MeetingsText.PROPOSED_Start, start));
        Children.Add(MeetingsWindow.Button(MeetingsText.PROPOSED_Meetings, list));
        Refresh();
    }
    public void Refresh() => IsVisible = _settings().Enabled;
}
