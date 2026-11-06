using Dictum.Core;
using Dictum.UI.Shell;

namespace Dictum.UI.Overlays;

/// <summary>
/// Shows each dictation state on what the settings ask for: the pill (pill.enabled), the orb and the screen-edge glow
/// (indicator.mode: off, orb, edge or both). Meetings always show the pill and halo. The meeting pill accepts input;
/// other overlays stay click-through. Showing never activates them. UI thread only.
/// </summary>
internal sealed class OverlayPresenter(AppShell shell) : IDisposable
{
    private readonly List<EdgeWindow> _edges = [];
    private PillWindow? _pill;
    private OrbWindow? _orb;
    private bool _edgesUp;

    /// <summary>For tests: the pill, once one has been shown.</summary>
    public PillWindow? Pill => _pill;

    public OrbWindow? Orb => _orb;

    public bool EdgesShown => _edgesUp;

    public void Show(PillState state)
    {
        var settings = shell.Settings;
        var meeting = state.View is PillView.MeetingRecording or PillView.MeetingPaused;
        if (state.View != PillView.Hidden && _pill is not null && _pill.MeetingControls != meeting)
        {
            _pill.Close();
            _pill = null;
        }
        if (settings.Pill.Enabled || meeting) (_pill ??= NewPill(meeting)).Present(state);
        else _pill?.Present(new PillState(PillView.Hidden, 0));

        if (settings.Indicator.ShowsOrb) (_orb ??= NewOrb()).Present(state.View, settings.Indicator.OrbCorner);
        else _orb?.Present(PillView.Hidden, settings.Indicator.OrbCorner);

        if (state.View != PillView.Busy) ShowEdges((settings.Indicator.ShowsEdge || meeting) && Overlay.IsRecording(state.View));
    }

    private PillWindow NewPill(bool meeting)
    {
        var pill = new PillWindow(shell.Platform, meeting);
        pill.SetLevelSource(() => shell.CurrentLevel);
        pill.PauseMeeting += shell.PauseMeetingFromPill;
        pill.StopMeeting += shell.StopMeetingFromPill;
        pill.SetLabelSource(view => view switch
        {
            PillView.MeetingRecording => Dictum.UI.Views.Meetings.MeetingsText.PROPOSED_MeetingRecording,
            PillView.MeetingPaused => Dictum.UI.Views.Meetings.MeetingsText.PROPOSED_MeetingPaused,
            _ => shell.PillLabel(view),
        }); // cloud speech: "Hearing… (cloud)", and the fallback line after a failed request
        return pill;
    }

    private OrbWindow NewOrb()
    {
        var orb = new OrbWindow(shell.Platform);
        orb.SetLevelSource(() => shell.CurrentLevel);
        return orb;
    }

    /// <summary>One glow per monitor, sized to it each time recording starts (monitors come and go).</summary>
    private void ShowEdges(bool show)
    {
        if (!show)
        {
            if (!_edgesUp) return;
            foreach (var edge in _edges) edge.Hide();
            _edgesUp = false;
            return;
        }
        if (_edgesUp) return;
        if (_edges.Count == 0) _edges.Add(new EdgeWindow(shell.Platform));
        var screens = _edges[0].Screens.All;
        while (_edges.Count < screens.Count) _edges.Add(new EdgeWindow(shell.Platform));
        for (var i = 0; i < _edges.Count; i++)
        {
            if (i >= screens.Count)
            {
                _edges[i].Hide();
                continue;
            }
            _edges[i].Cover(screens[i]);
            _edges[i].Show();
        }
        _edgesUp = true;
    }

    public void Dispose()
    {
        _pill?.Close();
        _orb?.Stop();
        foreach (var edge in _edges) edge.Close();
        _edges.Clear();
    }
}
