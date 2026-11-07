using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    /// <summary>Said when Save names meets a name <see cref="MeetingSpeakers.IsValidName"/> refuses; nothing is saved.</summary>
    public const string PROPOSED_InvalidName = "Use letters, numbers, spaces, full stops, hyphens and apostrophes in a name.";
    public const string PROPOSED_ModelsMissing = "Download the speaker model in Settings > Meetings first.";
    public const string PROPOSED_NothingToSeparate = "This meeting has no recording of other people to separate.";
    public const string PROPOSED_TooLittleSpeech = "Too little speech from other people to tell the voices apart.";
    public const string PROPOSED_SettingsRow = "Identify speakers";
    public const string PROPOSED_SettingsNote = "One model, downloaded once, lets Dictum tell the other voices in a recorded meeting apart. It runs on this computer.";
    public const string PROPOSED_ModelsReady = "Speaker model: downloaded";
    public const string PROPOSED_ModelsNotReady = "Speaker model: not downloaded";
    public const string PROPOSED_DownloadModels = "Download the speaker model";
    public const string PROPOSED_DownloadNote = "For Identify speakers in Meetings; the recording never leaves this computer";
    /// <summary>Shown beside the (separately approved) delete question, only when the meeting has a speakers file.</summary>
    public const string PROPOSED_NamesDeleted = "Names you gave the speakers in this meeting are deleted with it.";
}

/// <summary>One speaker turn: seconds from the meeting start, and its speaker (1 = the first voice heard).</summary>
public sealed record SpeakerTurn(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("speaker")] int Speaker);

/// <summary>A stretch of a one-track meeting that sounds like the owner's voice print (design 1.6 C.4): seconds from the meeting start.</summary>
public sealed record OwnerTurn(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End);

/// <summary>
/// speakers.json.enc (design 1.6 B): who spoke when on the separated track of one meeting, and the names the user gave them.
/// It lives only inside the meeting folder, sealed like every other file there; nothing about a guest exists outside it. No voice
/// embedding is stored: a separation run computes them in memory and drops them.
/// </summary>
/// <param name="You">
/// Design 1.6 C.4: on a one-track meeting separated with the owner's voice print, the turns that sounded like the owner; they are
/// "You" (speaker 0, reserved) and were never clustered. Absent (null) means none; the version stays 1.
/// </param>
public sealed record MeetingSpeakers(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("turns")] IReadOnlyList<SpeakerTurn> Turns,
    [property: JsonPropertyName("names")] IReadOnlyDictionary<string, string> Names,
    [property: JsonPropertyName("you"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OwnerTurn>? You = null)
{
    public const int CurrentVersion = 1;

    /// <summary>The speaker number of the owner's turns (design 1.6 C.4): reserved, so "Other n" keep starting at 1.</summary>
    public const int YouSpeaker = 0;

    /// <summary>
    /// The label of the owner's turns, exactly as a You channel segment is written today (its channel name, as ExportMarkdown and the
    /// summary chunk citations write it).
    /// </summary>
    public static readonly string YouLabel = Channel.You.ToString();

    /// <summary>The meeting has turns labelled You by the voice print.</summary>
    [JsonIgnore] public bool HasYou => You is { Count: > 0 };
    public const double DefaultThreshold = 0.5;
    public const double ThresholdStep = 0.1;
    public const int MaxNameLength = 80;

    /// <summary>
    /// The characters a name may hold, as the body of a regex character class: exactly the ones the summary validator accepts in an
    /// action-item owner (letters, digits, white space, full stop, the two apostrophes, hyphen), so every saved name can own one.
    /// </summary>
    public const string NameCharacters = @"\p{L}\p{Nd}\s.'’\-";

    private static readonly Regex NameShape = new("^[" + NameCharacters + @"]+\z", RegexOptions.CultureInvariant);

    // "Other n" as Label writes it, with any number: a name shaped like a label would read as another speaker.
    private static readonly Regex LabelShape =
        new("^" + Regex.Escape(SpeakerText.PROPOSED_Other).Replace(@"\{0}", "[0-9]+", StringComparison.Ordinal) + @"\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one test of a typed name, used on save (<see cref="WithNames"/>), on read (<see cref="Validated"/>) and by the name box:
    /// trimmed, 1 to <see cref="MaxNameLength"/> characters of <see cref="NameCharacters"/> with no control character, and not
    /// shaped like "Other n".
    /// </summary>
    public static bool IsValidName(string? name)
    {
        var trimmed = name?.Trim();
        return trimmed is { Length: >= 1 and <= MaxNameLength } && !trimmed.Any(char.IsControl) && NameShape.IsMatch(trimmed) && !LabelShape.IsMatch(trimmed);
    }

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
        foreach (var turn in You ?? [])
            if (turn is null || !double.IsFinite(turn.Start) || !double.IsFinite(turn.End) || turn.Start < 0 || turn.End < turn.Start)
                throw new InvalidDataException("Invalid speaker turn.");
        foreach (var (key, name) in Names)
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var speaker) || speaker < 1 || speaker > count || !IsValidName(name))
                throw new InvalidDataException("Invalid speaker name.");
        return this;
    }

    /// <summary>The name the user gave this speaker, else "Other n"; "You" for <see cref="YouSpeaker"/>.</summary>
    public string Label(int speaker) => speaker == YouSpeaker ? YouLabel : TypedName(speaker) ?? OtherLabel(speaker);

    /// <summary>"Other n" for any speaker number, named or not.</summary>
    public static string OtherLabel(int speaker) => string.Format(CultureInfo.InvariantCulture, SpeakerText.PROPOSED_Other, speaker);

    /// <summary>The trimmed name the user typed for this speaker, or null if none.</summary>
    public string? TypedName(int speaker) =>
        Names.TryGetValue(speaker.ToString(CultureInfo.InvariantCulture), out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;

    /// <summary>The names typed for this meeting's speakers 1 to <see cref="Count"/>.</summary>
    [JsonIgnore] public IEnumerable<string> TypedNames =>
        Enumerable.Range(1, Count).Select(TypedName).OfType<string>();

    /// <summary>
    /// Owner decision (Kyle, 07/11/2026): true when the whole text (trimmed) is exactly one of this meeting's speakers: "Other 1" to
    /// "Other {Count}" as <see cref="Label"/> writes it, or a name as typed for one of them, compared ordinally (case counts).
    /// Nothing partial, nothing beyond the count. Design 1.6 C.4: "You" too, but only when the meeting has turns the voice print
    /// labelled You (it is the user).
    /// </summary>
    public bool IsSpeaker(string text)
    {
        var owner = text.Trim();
        return HasYou && string.Equals(owner, YouLabel, StringComparison.Ordinal) ||
            Enumerable.Range(1, Count).Any(n => string.Equals(owner, OtherLabel(n), StringComparison.Ordinal)) ||
            TypedNames.Any(name => string.Equals(owner, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The speaker of a transcript segment: the turn it overlaps most, else the nearest turn within a second. The turns are the
    /// separated track's own transcript segments, so a segment normally finds itself; the nearest-turn rule covers one too short
    /// to embed. The You track is never separated, so a You segment never has one; null when no turn is near. A turn the voice print
    /// labelled You (C.4) counts like any other, as <see cref="YouSpeaker"/>.
    /// </summary>
    public int? SpeakerOf(Segment segment)
    {
        // Typed notes are zero-length Mixed segments ("Take notes instead"): nobody spoke them, so they keep the channel label.
        var turns = Turns.Select(t => (t.Start, t.End, t.Speaker)).Concat((You ?? []).Select(t => (t.Start, t.End, Speaker: YouSpeaker))).ToList();
        if (segment.Channel == Channel.You || segment.End <= segment.Start || turns.Count == 0) return null;
        double start = segment.Start.TotalSeconds, end = Math.Max(segment.End.TotalSeconds, start);
        var best = turns.Select(t => (t.Speaker, Overlap: Math.Min(end, t.End) - Math.Max(start, t.Start))).MaxBy(t => t.Overlap);
        if (best.Overlap > 0) return best.Speaker;
        var nearest = turns.Select(t => (t.Speaker, Gap: t.End < start ? start - t.End : t.Start - end)).MinBy(t => t.Gap);
        return nearest.Gap <= 1.0 ? nearest.Speaker : null;
    }

    /// <summary>The label of a transcript segment: its speaker's name or "Other n", or null to keep the channel's own label.</summary>
    public string? LabelOf(Segment segment) => SpeakerOf(segment) is { } speaker ? Label(speaker) : null;

    /// <summary>
    /// This separation with these names (blank ones removed, the rest trimmed), for the store to write. A name
    /// <see cref="IsValidName"/> refuses throws ArgumentException: the name box checks first and says why.
    /// </summary>
    public MeetingSpeakers WithNames(IReadOnlyDictionary<int, string> names)
    {
        var given = names.Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToList();
        if (given.Any(n => !IsValidName(n.Value))) throw new ArgumentException(SpeakerText.PROPOSED_InvalidName, nameof(names));
        return this with { Names = given.ToDictionary(n => n.Key.ToString(CultureInfo.InvariantCulture), n => n.Value.Trim()) };
    }
}
