using System.Text;

namespace Dictum.Personal;

/// <summary>
/// context.txt, "About you" in Settings > Personal (21/10/2026, SPEC §16.7): a sentence or two about who the speaker is and what
/// they talk about, given to the clean-up model so it picks the right spellings and terms ("I am a financial planner in the
/// United Kingdom"). One paragraph: every run of whitespace, line breaks included, is read as one space, and '#' lines are
/// comments, as in the other personal files. At most <see cref="MaxChars"/> characters; a longer file is left out of the
/// prompt whole (with a warning that gives its length only), never cut, so the model never reads half a sentence. Used in the
/// clean-up prompt only, never in whisper's prompt (DECISIONS, 21/10/2026). Never logged: only its length is.
/// </summary>
public sealed class SpeakerContext
{
    /// <summary>
    /// The cap. 500 characters is two or three sentences, and with the line's start about 600 characters of the 4,096-token
    /// context the prompt shares with the dictation, beside the dictionary line's 1,000 (<see cref="PersonalDictionary.CleanupLineChars"/>).
    /// </summary>
    public const int MaxChars = 500;

    public const string FileHeader =
        "Dictum About you: a sentence or two about who you are and what you talk about (Settings > Personal).\n" +
        "The clean-up model uses it to choose spellings and terms. One paragraph, at most 500 characters.";

    public static readonly SpeakerContext Empty = new(null);

    private SpeakerContext(string? text) => Text = text;

    /// <summary>The paragraph as the prompt reads it, or null when there is none (or the file is over the cap).</summary>
    public string? Text { get; }

    /// <summary>One paragraph: line breaks, tabs and runs of spaces become one space; trimmed.</summary>
    public static string Normalise(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Why <paramref name="text"/> (already normalised) cannot be saved, or null.</summary>
    public static string? Check(string text) =>
        text.Length > MaxChars ? $"This is {text.Length} characters; the limit is {MaxChars}. Shorten it and save again."
        : text.StartsWith('#') ? "About you cannot start with #: in context.txt that marks a comment line."
        : null;

    /// <summary>From a paragraph typed in Settings or read from the file; text over the cap gives none.</summary>
    public static SpeakerContext Of(string? text)
    {
        var paragraph = Normalise(text);
        return paragraph.Length == 0 || Check(paragraph) is not null ? Empty : new SpeakerContext(paragraph);
    }

    /// <summary>The file's text (its non-comment lines as one paragraph); a missing or unreadable file is none.</summary>
    public static SpeakerContext Load(string path, Action<string>? warn = null)
    {
        var paragraph = Normalise(string.Join(' ', TextList.Read(path, warn)));
        if (paragraph.Length > MaxChars)
            warn?.Invoke($"{Path.GetFileName(path)} is {paragraph.Length} characters, over the {MaxChars}-character limit; it is left out of the clean-up until it is shortened.");
        return Of(paragraph);
    }

    /// <summary>What the file holds for Settings to show: the paragraph even when it is over the cap, so it can be shortened.</summary>
    public static string ReadForEditing(string path) => Normalise(string.Join(' ', TextList.Read(path)));

    /// <summary>
    /// Writes the paragraph with its header, replacing the file; empty text deletes the file (nothing about the speaker stays
    /// on disk, and the prompt goes back to having no context line). Text over the cap is refused by the caller first.
    /// </summary>
    public static void Save(string path, string paragraph)
    {
        if (paragraph.Length == 0)
        {
            File.Delete(path); // no error when it is not there
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        foreach (var h in FileHeader.Split('\n')) sb.Append("# ").Append(h).Append(Environment.NewLine);
        sb.Append(paragraph).Append(Environment.NewLine);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
