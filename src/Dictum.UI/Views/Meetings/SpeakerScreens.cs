using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Meetings;
using Dictum.Meetings.Speakers;
using static Dictum.Meetings.Speakers.SpeakerText;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.UI.Views.Meetings;

/// <summary>
/// The meeting screen's speaker card (design 1.6 B): Identify speakers, its progress, a labelled name box for each "Other n", and
/// Fewer / More speakers, which run the separation again one threshold step either way. Everything here stays in this meeting.
/// </summary>
internal sealed partial class MeetingsWindow
{
    /// <summary>A transcript line: the speaker's name or "Other n" on a separated meeting, else the channel (You, Others, Mixed).</summary>
    internal static string FormatSegment(Segment segment, MeetingSpeakers? speakers = null) => string.Format(PROPOSED_Segment, segment.Start,
        speakers?.LabelOf(segment) ?? segment.Channel switch { Channel.You => PROPOSED_YouChannel, Channel.Others => PROPOSED_OthersChannel, _ => PROPOSED_MixedChannel },
        segment.Text);

    /// <summary>
    /// The delete confirmation's extra sentence about speaker names, or null when the meeting has no speakers file. A file this
    /// version cannot read (the store reads it as none) still counts: deleting the meeting deletes it either way.
    /// </summary>
    private string? NamesDeletedNote(MeetingId id) => _host.Store.HasSpeakers(id) ? PROPOSED_NamesDeleted : null;

    private Control SpeakersCard(MeetingId id, MeetingSpeakers? speakers)
    {
        Channel? track;
        // A damaged or unreadable track must not stop the meeting screen opening: separation is then simply not offered.
        try { track = SpeakerSeparator.TrackFor(_host.Store, id); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException) { track = null; }
        var progress = Named(new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false }, PROPOSED_Separating);
        var children = new List<Control>();
        if (track is null) children.Add(Ui.Text(PROPOSED_NothingToSeparate)); // audio deleted by retention, or never recorded: names stay editable
        else children.AddRange([SafeButton(speakers is null ? PROPOSED_SeparateSpeakers : PROPOSED_SeparateAgain,
            () => Separate(id, speakers?.Threshold ?? MeetingSpeakers.DefaultThreshold, progress)), Ui.Text(PROPOSED_Limits, "caption"), progress]);
        if (speakers is not null)
        {
            children.Add(Ui.Text(string.Format(CultureInfo.InvariantCulture, PROPOSED_Separated, speakers.Count)));
            var boxes = new Dictionary<int, TextBox>();
            for (var speaker = 1; speaker <= speakers.Count; speaker++)
            {
                var other = string.Format(CultureInfo.InvariantCulture, PROPOSED_Other, speaker);
                var name = speakers.Names.TryGetValue(speaker.ToString(CultureInfo.InvariantCulture), out var given) ? given : "";
                boxes[speaker] = new TextBox { Text = name, Watermark = other, MaxLength = MeetingSpeakers.MaxNameLength, MinWidth = 240 };
                children.Add(Ui.Line(string.Format(CultureInfo.InvariantCulture, PROPOSED_RenameSpeaker, other), boxes[speaker])); // the label names the box
            }
            children.Add(SafeButton(PROPOSED_SaveNames, () =>
            {
                // One test of a name (MeetingSpeakers.IsValidName): a name the summary validator could not accept as an owner, or one
                // shaped like "Other n", is refused here with the reason, and nothing is saved.
                if (boxes.Values.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Text) && !MeetingSpeakers.IsValidName(b.Text)) is { } refused)
                {
                    Say(PROPOSED_InvalidName);
                    refused.Focus();
                    return;
                }
                _host.Store.WriteSpeakers(id, speakers.WithNames(boxes.ToDictionary(b => b.Key, b => b.Value.Text ?? "")));
                ShowMeeting(id); Say(PROPOSED_NamesSaved);
            }));
            if (track is not null) children.AddRange([Ui.Text(PROPOSED_SeparateAgain, "caption"), Ui.Row(8,
                SafeButton(PROPOSED_FewerSpeakers, () => Separate(id, SpeakerSeparator.Step(speakers.Threshold, fewer: true), progress)),
                SafeButton(PROPOSED_MoreSpeakers, () => Separate(id, SpeakerSeparator.Step(speakers.Threshold, fewer: false), progress)))]);
        }
        return Ui.Card(Ui.Stack(8, [.. children]), PROPOSED_SeparateSpeakers);
    }

    private void Separate(MeetingId id, double threshold, ProgressBar progress)
    {
        if (!_host.SpeakerModelsReady()) { Say(PROPOSED_ModelsMissing); return; }
        _ = Work(async () =>
        {
            progress.IsVisible = true;
            Say(PROPOSED_Separating);
            var report = new Progress<double>(value => Dispatcher.UIThread.Post(() => progress.Value = value));
            var result = await _host.SeparateSpeakers(id, threshold, report, _lifetime.Token);
            ShowMeeting(id);
            Say(string.Format(CultureInfo.InvariantCulture, PROPOSED_Separated, result.Count));
        });
    }
}
