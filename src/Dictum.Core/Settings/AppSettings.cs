namespace Dictum.Settings;

/// <summary>settings.json, SPEC §9. Immutable snapshot; a reload produces a new instance.</summary>
public sealed record AppSettings
{
    public bool Enabled { get; init; } = true;
    public string Hotkey { get; init; } = "RightCtrl";
    public bool SuppressHotkey { get; init; } = true;
    public int TapMs { get; init; } = 300;
    public int DoubleTapMs { get; init; } = 400;
    public int MinRecordingMs { get; init; } = 300;
    public int HandsFreeMaxSeconds { get; init; } = 300;
    public double SilenceThreshold { get; init; } = 0.01;
    public AudioSettings Audio { get; init; } = new();
    public WhisperSettings Whisper { get; init; } = new();
    public LlmSettings Llm { get; init; } = new();
    public InsertSettings Insert { get; init; } = new();
    public PillSettings Pill { get; init; } = new();
    public IndicatorSettings Indicator { get; init; } = new();
    public SoundSettings Sounds { get; init; } = new();
    public ProfanitySettings Profanity { get; init; } = new();

    /// <summary>Optional second hotkey that opens "Fix last dictation" (SPEC §16.3). Null = unset (the default).</summary>
    public string? FixHotkey { get; init; }

    public string LogLevel { get; init; } = "info";
    public bool LogTranscripts { get; init; }

    /// <summary>true: no network call of any kind (cloud providers refused, downloads refused). SPEC §5.</summary>
    public bool OfflineOnly { get; init; }

    /// <summary>The pill or a big indicator (orb, screen edge) shows the dictation state, so the controller sends it.</summary>
    public bool ShowsDictationState => Pill.Enabled || Indicator.ShowsOrb || Indicator.ShowsEdge;
}

public sealed record AudioSettings
{
    /// <summary>Null = Windows default capture device; otherwise a device ID or (part of) its friendly name.</summary>
    public string? Device { get; init; }
}

public sealed record WhisperSettings
{
    public string Model { get; init; } = "medium.en";
    public string? ModelsDir { get; init; }
    public string Language { get; init; } = "en";
    public int Threads { get; init; }
    /// <summary>"auto" (Vulkan then CPU), "vulkan" (same order, Vulkan preferred) or "cpu".</summary>
    public string Gpu { get; init; } = "auto";

    /// <summary>0 = greedy decoding (the default); 1 or more = beam search with this many beams (SPEC §16.5).</summary>
    public int BeamSize { get; init; }
}

public sealed record LlmSettings
{
    public const string Local = "local";
    public const string Anthropic = "anthropic";
    public const string OpenAiCompatible = "openai-compatible";
    public const string NoProvider = "none";
    public const string AnthropicDefaultModel = "claude-haiku-5-5";

    /// <summary>local (default, llama.cpp in-process), anthropic, openai-compatible, or none (rule-based tidy only).</summary>
    public string Provider { get; init; } = Local;

    /// <summary>local: a cleanup catalogue name or an absolute .gguf path; cloud providers: the model id.</summary>
    public string Model { get; init; } = "qwen2.5-1.5b";

    /// <summary>local provider backend: "auto" / "vulkan" (Vulkan, then CPU) or "cpu".</summary>
    public string Gpu { get; init; } = "auto";
    public string? BaseUrl { get; init; }
    public int TimeoutMs { get; init; } = 15000;
    public string? PromptFile { get; init; }

    /// <summary>true: the cleanup prompt also asks for article, tense, preposition and word-order fixes (SPEC §16.5).</summary>
    public bool GrammarAssist { get; init; }

    /// <summary>
    /// The host the user agreed, in Dictum's consent dialog, to send dictations to for cloud clean-up (SPEC §22): a cloud
    /// provider runs only while this names the host it would send to. Null = no consent given. Filled at load from
    /// consent.json (<see cref="CloudConsentFile"/>), which only that dialog writes; never read from settings.json.
    /// </summary>
    public string? CloudConsent { get; init; }
}

public sealed record InsertSettings
{
    public const string Paste = "paste";
    public const string Type = "type";

    public string Method { get; init; } = Paste;
    public bool RestoreClipboard { get; init; }
    public bool TrailingSpace { get; init; } = true;
    public int PasteDelayMs { get; init; } = 150;

    /// <summary>false (default): transcripts are marked to stay out of Win+V clipboard history and cloud clipboard sync.</summary>
    public bool ClipboardHistory { get; init; }
}

public sealed record PillSettings
{
    public bool Enabled { get; init; } = true;
}

/// <summary>The big recording indicators (docs/design/Indicators.dc.html): the orb, the screen-edge glow, both or neither.</summary>
public sealed record IndicatorSettings
{
    public const string Off = "off";
    public const string Orb = "orb";
    public const string Edge = "edge";
    public const string Both = "both";
    public static readonly string[] Modes = [Off, Orb, Edge, Both];
    public static readonly string[] Corners = ["bottom-right", "bottom-left", "top-right", "top-left"];

    public string Mode { get; init; } = Off;

    /// <summary>Where the orb sits on the monitor with the focused window. It is click-through, so it is placed, not dragged.</summary>
    public string OrbCorner { get; init; } = "bottom-right";

    public bool ShowsOrb => Mode is Orb or Both;
    public bool ShowsEdge => Mode is Edge or Both;
}

/// <summary>A short sound when recording starts and stops.</summary>
public sealed record SoundSettings
{
    public bool Enabled { get; init; }
}

public sealed record ProfanitySettings
{
    public const string Off = "off";
    public const string Mask = "mask";
    public const string Remove = "remove";

    /// <summary>off (default), mask ("f***") or remove (SPEC §16.4).</summary>
    public string Mode { get; init; } = Off;
}
