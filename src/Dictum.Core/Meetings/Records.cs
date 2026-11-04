using System.Text.Json.Serialization;

namespace Dictum.Meetings;

public readonly record struct MeetingId : IComparable<MeetingId>
{
    public string Value { get; }
    [JsonConstructor]
    public MeetingId(string value)
    {
        if (value is null || value.Length != 32 || value.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Invalid meeting id.", nameof(value));
        Value = value;
    }
    public static MeetingId New() => new(Guid.NewGuid().ToString("N"));
    public int CompareTo(MeetingId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;
}

public enum Channel { You, Others, Mixed }
public record Segment(TimeSpan Start, TimeSpan End, Channel Channel, string Text);
public enum MarkerKind { Moment, Joined, Paused, Resumed }
public record Marker(TimeSpan At, MarkerKind Kind);
public enum AgreementKind { Live, Import }
public record Agreement
{
    public DateTimeOffset AcknowledgedAt { get; init; }
    public string How { get; init; }
    public string ScriptVersion { get; init; }
    public AgreementKind Kind { get; init; }
    public Agreement(DateTimeOffset acknowledgedAt, string how, string scriptVersion, AgreementKind kind)
    {
        if (how is not ("spoken" or "written" or "platform-announced")) throw new ArgumentException("Invalid agreement method.", nameof(how));
        (AcknowledgedAt, How, ScriptVersion, Kind) = (acknowledgedAt, how, scriptVersion, kind);
    }
}
public enum MeetingSource { Recorded, ImportedZoom, ImportedFile }
public record MeetingMeta(MeetingId Id, string Title, DateTimeOffset StartedAt, TimeSpan Duration,
    MeetingSource Source, Agreement? Agreement, IReadOnlyList<Marker> Markers,
    IReadOnlyList<KeyWrap> Wraps, bool HasVideo, string? SummaryEngine, string? SummaryTemplate);
public record KeyWrap(string RecipientId, byte[] WrappedKey);
public record Summary(string Markdown, string Engine, string Template, DateTimeOffset At, IReadOnlyList<ActionItem> Items);
public record ActionItem(string Owner, string Text);
public enum KeepAudio { Forever, Days30, Days90 }
public enum ZoomImport { Ask, Always, Never }
public enum SummaryEngine { Local, Ollama, OwnKey }
public enum ShareDefault { Nobody, Team, Ask }
public record MeetingsSettings
{
    public bool Enabled { get; init; }
    public string? Folder { get; init; }
    public KeepAudio KeepAudio { get; init; } = KeepAudio.Forever;
    public ZoomImport ZoomImport { get; init; } = ZoomImport.Ask;
    public string? ZoomFolder { get; init; }
    public SummaryEngine SummaryEngine { get; init; } = SummaryEngine.Local;
    public string SummaryTemplate { get; init; } = "general";
    public ShareDefault ShareDefault { get; init; } = ShareDefault.Nobody;
}
