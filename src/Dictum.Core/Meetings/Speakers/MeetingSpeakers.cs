using System.Globalization;
using System.Text.Json.Serialization;

namespace Dictum.Meetings.Speakers;

/// <summary>New user-facing words for speaker separation (design 1.6 B). PROPOSED: the owner approves or rewords them.</summary>
public static class SpeakerText
{
    public const string PROPOSED_Other = "Other {0}";
    public const string PROPOSED_SeparateSpeakers = "Identify speakers";
    public const string PROPOSED_SeparateAgain = "Identify speakers again";
    public const string PROPOSED_FewerSpeakers = "Fewer speakers";
    public const string PROPOSED_MoreSpeakers = "More speakers";
    public const string PROPOSED_Limits = "Two similar voices on one line may be merged. Names apply to this meeting only.";
    public const string PROPOSED_Separating = "Identifying speakers…";
    public const string PROPOSED_Separated = "Speakers identified: {0}.";
    public const string PROPOSED_RenameSpeaker = "Name for {0}";
    public const string PROPOSED_SaveNames = "Save names";
    public const string PROPOSED_NamesSaved = "Names saved for this meeting.";
    public const string PROPOSED_ModelsMissing = "Download the speaker models in Settings > Meetings first.";
    public const string PROPOSED_NothingToSeparate = "This meeting has no recording of other people to separate.";
    public const string PROPOSED_SettingsRow = "Identify speakers";
    public const string PROPOSED_SettingsNote = "Two models, downloaded once, let Dictum tell the other voices in a recorded meeting apart. They run on this computer.";
    public const string PROPOSED_ModelsReady = "Speaker models: downloaded";
    public const string PROPOSED_ModelsNotReady = "Speaker models: not downloaded";
    public const string PROPOSED_DownloadModels = "Download the speaker models";
    public const string PROPOSED_DownloadNote = "For Identify speakers in Meetings; the recording never leaves this computer";
    /// <summary>Shown beside the (separately approved) delete question, only when the meeting has a speakers file.</summary>
    public const string PROPOSED_NamesDeleted = "Names you gave the speakers in this meeting are deleted with it.";
}

/// <summary>One speaker turn: seconds from the meeting start, and its speaker (1 = the first voice heard).</summary>
public sealed record SpeakerTurn(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("speaker")] int Speaker);

/// <summary>
/// speakers.json.enc (design 1.6 B): who spoke when on the separated track of one meeting, and the names the user gave them.
/// It lives only inside the meeting folder, sealed like every other file there; nothing about a guest exists outside it. No voice
/// embedding is stored: a separation run computes them in memory and drops them.
/// </summary>
public sealed record MeetingSpeakers(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("turns")] IReadOnlyList<SpeakerTurn> Turns,
    [property: JsonPropertyName("names")] IReadOnlyDictionary<string, string> Names)
{
    public const int CurrentVersion = 1;
    public const double DefaultThreshold = 0.5;
    public const double ThresholdStep = 0.1;
    public const int MaxNameLength = 80;

    /// <summary>How many voices were found.</summary>
    [JsonIgnore] public int Count => Turns.Count == 0 ? 0 : Turns.Max(t => t.Speaker);

    /// <summary>Throws InvalidDataException for anything the store must not accept (also on read: a file is data, not trusted).</summary>
    public MeetingSpeakers Validated()
    {
        if (Version != CurrentVersion || string.IsNullOrWhiteSpace(Model) || Turns is null || Names is null ||
            double.IsNaN(Threshold) || Threshold is < 0 or > 2)
            throw new InvalidDataException("Invalid speakers file.");
        var count = Count;
        foreach (var turn in Turns)
            if (turn is null || !double.IsFinite(turn.Start) || !double.IsFinite(turn.End) || turn.Start < 0 || turn.End < turn.Start || turn.Speaker < 1)
                throw new InvalidDataException("Invalid speaker turn.");
        foreach (var (key, name) in Names)
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var speaker) || speaker < 1 || speaker > count ||
                name is null || name.Length > MaxNameLength || name.Any(char.IsControl))
                throw new InvalidDataException("Invalid speaker name.");
        return this;
    }

    /// <summary>The name the user gave this speaker, else "Other n".</summary>
    public string Label(int speaker) => TypedName(speaker) ?? OtherLabel(speaker);

    /// <summary>"Other n" for any speaker number, named or not.</summary>
    public static string OtherLabel(int speaker) => string.Format(CultureInfo.InvariantCulture, SpeakerText.PROPOSED_Other, speaker);

    /// <summary>The trimmed name the user typed for this speaker, or null if none.</summary>
    public string? TypedName(int speaker) =>
        Names.TryGetValue(speaker.ToString(CultureInfo.InvariantCulture), out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;

    /// <summary>The names typed for this meeting's speakers 1 to <see cref="Count"/>.</summary>
    [JsonIgnore] public IEnumerable<string> TypedNames =>
        Enumerable.Range(1, Count).Select(TypedName).OfType<string>();

    /// <summary>
    /// Owner decision (Kyle, 07/11/2026): true when the whole text (trimmed, any case) is one of this meeting's speakers: "Other 1" to
    /// "Other {Count}" exactly as <see cref="Label"/> writes it, or a name typed for one of them. Nothing partial, nothing beyond the count.
    /// </summary>
    public bool IsSpeaker(string text)
    {
        var owner = text.Trim();
        return Enumerable.Range(1, Count).Any(n => string.Equals(owner, OtherLabel(n), StringComparison.OrdinalIgnoreCase)) ||
            TypedNames.Any(name => string.Equals(owner, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The speaker of a transcript segment: the turn it overlaps most, else the nearest turn within a second (whisper's segment
    /// times and the segmentation's turn times are both approximate). The You track is never separated, so a You segment never
    /// has one; null when no turn is near.
    /// </summary>
    public int? SpeakerOf(Segment segment)
    {
        // Typed notes are zero-length Mixed segments ("Take notes instead"): nobody spoke them, so they keep the channel label.
        if (segment.Channel == Channel.You || segment.End <= segment.Start || Turns.Count == 0) return null;
        double start = segment.Start.TotalSeconds, end = Math.Max(segment.End.TotalSeconds, start);
        var best = Turns.Select(t => (t.Speaker, Overlap: Math.Min(end, t.End) - Math.Max(start, t.Start))).MaxBy(t => t.Overlap);
        if (best.Overlap > 0) return best.Speaker;
        var nearest = Turns.Select(t => (t.Speaker, Gap: t.End < start ? start - t.End : t.Start - end)).MinBy(t => t.Gap);
        return nearest.Gap <= 1.0 ? nearest.Speaker : null;
    }

    /// <summary>The label of a transcript segment: its speaker's name or "Other n", or null to keep the channel's own label.</summary>
    public string? LabelOf(Segment segment) => SpeakerOf(segment) is { } speaker ? Label(speaker) : null;

    /// <summary>This separation with these names (blank ones removed), for the store to write.</summary>
    public MeetingSpeakers WithNames(IReadOnlyDictionary<int, string> names) => this with
    {
        Names = names.Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key.ToString(CultureInfo.InvariantCulture), n => n.Value.Trim()),
    };
}
