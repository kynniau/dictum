using Dictum.Core;
using Dictum.Llm;
using Dictum.Settings;

namespace Dictum.Help;

/// <summary>
/// The words on Settings → Help (SPEC §21; docs/DECISIONS.md, "In-app Help", 21/10/2026). APPROVED 22/10/2026 copy (proposed 21/10/2026):
/// do not reword. Plain constants with no platform code, so tests can pin
/// them and a Mac build can reuse the parts that hold there.
/// <para>
/// Written for someone who has never used a computer program like this: short sentences, a technical word explained in
/// the same sentence, Australian English, no marketing. Every answer matches what Dictum does as built: the labels it
/// names ("Calibrate my voice", "Try on the CPU", "Mic error"…) are pinned against the real windows by HelpPageTests, so
/// renaming one in the app fails a test until this text follows it.
/// </para>
/// The page reaches the network only through its buttons, which hand <see cref="GuideUrl"/> or a mail link built by
/// <see cref="SupportMail"/> to the system (IPlatform.Open): the browser or mail app does the rest, not Dictum.
/// </summary>
public static class HelpText
{
    public const string PageTitle = "Help";

    /// <summary>The full illustrated guide (built and hosted outside the app). Opened by the system browser, never fetched by Dictum.</summary>
    public const string GuideUrl = "https://dictum.kynni.au/help";

    public const string MoreHelpHeading = "More help";

    public const string GuideButton = "Open the full guide";

    public const string GuideNote = "The full guide on the Dictum website goes step by step, with pictures. It opens in your web browser and needs the internet.";

    public const string SupportButton = "Email support";

    /// <summary>APPROVED 22/10/2026 copy (proposed 21/10/2026): do not reword. Email support opens the same prefilled email as Report a problem.</summary>
    public const string SupportNote = "Email support opens a new email to " + SupportMail.Address + " in your own email program, with the version of Dictum and the models in use filled in. Dictum sends nothing by itself.";

    /// <summary>APPROVED 22/10/2026 copy (proposed 21/10/2026): do not reword.</summary>
    public const string FeedbackButton = "Send feedback or suggest a feature";

    /// <summary>APPROVED 22/10/2026 copy (proposed 21/10/2026): do not reword.</summary>
    public const string FeedbackNote = FeedbackButton + " opens a new email to " + SupportMail.Address + " in your own email program, with the version of Dictum, your Windows build and the models in use filled in. " +
        "You can read and change it before you send it. Dictum sends nothing by itself.";

    public const string HowItWorksHeading = "How Dictum works";

    /// <summary>"How Dictum works": one entry per paragraph, in order.</summary>
    public static readonly IReadOnlyList<string> HowItWorks =
    [
        "Dictum types what you say into the program you are using.",
        "1. Click where you want the words to go, for example in an email.",
        "2. Hold down the Dictum key. Unless you changed it, this is the Ctrl key on the right-hand side of the keyboard.",
        "3. Speak in your normal voice. A small bar above the taskbar shows that Dictum is listening.",
        "4. Let go of the key. A moment later, your words appear where you clicked.",
        "In that moment, three things happen. First, your voice is turned into text on this PC by a speech model. A speech model is a program that has learned what spoken words sound like. " +
        "Next, the text is tidied up: punctuation and capital letters go in, and ums and ahs come out. Last, the text is pasted where you clicked, as if you had typed it.",
        // APPROVED 22/10/2026 (proposed 21/10/2026, final round on the Simple view): the clean-up choices as Settings > Models names them.
        "Tidy my words, in Settings > Models, does the tidying. Unless the spoken language is set to English (or Translate to English is on), only the spacing is tidied. " +
        "To paste what was heard with no tidying, choose Off there instead; only your own corrections, snippets and the profanity filter (if on) still apply.",
        // APPROVED 22/10/2026 18:41 AEDT (qualifier added; proposed 21/10/2026): "or cloud speech" in the first sentence; the second
        // sentence's qualifier APPROVED 22/10/2026 17:22 AEDT (qualifier added; proposed 21/10/2026, final round): the second sentence says what cloud clean-up sends, as "Where does my voice go?" does; the qualifier "unless you also turn on cloud speech" is the owner's.
        "All of this happens on this PC. Nothing you say is sent anywhere unless you turn on cloud clean-up or cloud speech in Settings > Clean-up, and it is off unless you turn it on. " +
        "If you turn it on, the text of each dictation and the words in your dictionary go to the service you chose; your voice still stays here unless you also turn on cloud speech.",
        "The Dictum menu: click the Dictum icon near the clock, usually at the bottom right of the screen. If you cannot see the icon, click the small up arrow beside the clock first. " +
        "The menu has Settings, Help and more.",
        // APPROVED 22/10/2026 (proposed 21/10/2026, final lens): the help names the Simple view's choices, so it says how to get that view.
        "In this help, \"Settings > Models\" means: open Settings from the Dictum menu, then choose Models on the left. " +
        "This help names the choices as the Simple view shows them; if you see model names instead, choose Simple at the top of that page.",
    ];

    public const string AccuracyHeading = "Why Dictum hears some people better than others";

    /// <summary>The part of "How Dictum works" on accuracy: one entry per paragraph.</summary>
    public static readonly IReadOnlyList<string> Accuracy =
    [
        "Everyone's voice, accent and microphone are different, so a speech model hears some people better than others.",
        // APPROVED 22/10/2026 (proposed 21/10/2026, final round on the Simple view): the speech choices as the Simple view of Settings > Models names them.
        "For English, the speech model that comes with Dictum is a good place to start: it is Standard in Settings > Models. For other languages, Standard is a multilingual model, a download you choose.",
        "If words often come out wrong, open Settings > Models and choose More accurate, or Most accurate where it is listed. These are bigger speech models: more accurate, but slower, and each one is a download you choose. " +
        "To see every model by its name and size, choose Advanced at the top of that page.",
        "Not sure which one to choose? Open the Dictum menu and choose Calibrate my voice. You read ten short sentences aloud, and Dictum tells you which of the speech models on this PC hears you best.",
    ];

    public const string QuestionsHeading = "Questions and answers";

    /// <summary>
    /// The list at the top of the page, each question a link to its answer (APPROVED 22/10/2026; proposed 1.2 polish, 22/10/2026). The questions are
    /// <see cref="Questions"/> themselves: the list holds no words of its own but this heading and the links' screen-reader hint.
    /// </summary>
    public const string IndexHeading = "Questions on this page";

    /// <summary>What a screen reader says with each link in the list (APPROVED 22/10/2026; proposed 1.2 polish).</summary>
    public const string IndexLinkHelp = "Moves to the answer, further down this page.";

    /// <summary>APPROVED 22/10/2026 (proposed 21/10/2026): the 1.1.2 note's sentence on the start-up crash, word for word (docs/STORE.md). The one place the Help describes it.</summary>
    public const string CrashFixLine = "Dictum 1.1.2 fixes a start-up crash on PCs without a working Vulkan graphics driver (the app closed before its tray icon appeared; it now runs the speech model on the CPU there).";

    /// <summary>APPROVED verbatim for Help, site and Store (owner, 21/10/2026 22:18 AEDT): do not reword. HelpPageTests exempts exactly this line from the "no telemetry or analytics word" pin.</summary>
    public const string FreeLine = "Free: no subscription, no account, no ads, no analytics, no telemetry.";

    /// <summary>APPROVED verbatim for Help, site and Store (owner, 21/10/2026 22:18 AEDT): do not reword.</summary>
    public const string FreeLaterNote = "The base product stays free to keep on your PC; optional paid extras may come later.";

    /// <summary>APPROVED 22/10/2026 (proposed 22/10/2026): the step-by-step answer for the optional cloud clean-up.</summary>
    public const string CloudQuestion = "How do I turn on cloud clean-up?";

    /// <summary>The host the consent dialog names for Anthropic, so the warning below is quoted exactly as that dialog shows it.</summary>
    public static readonly string CloudExampleHost = CloudPolicy.CloudHost(new LlmSettings { Provider = LlmSettings.Anthropic })!;

    /// <summary>
    /// APPROVED 22/10/2026 (proposed 22/10/2026, lens fixes on 2b20b0f); first paragraph APPROVED 22/10/2026 17:22 AEDT (qualifier
    /// added: "unless you also turn on cloud speech"): do not reword. The approved warning, the
    /// waiting-for-key line and the tray line (<see cref="CloudPolicy.TrayLine"/>) are quoted word for word from the app's own copy;
    /// the variable names are SecretStore's. Steps 2 to 4 and 7 to 15 are one action each, in the cloud-cleanup demo's caption order.
    /// </summary>
    public static readonly IReadOnlyList<string> CloudAnswer =
    [
        "Cloud clean-up is an advanced option, and it is off unless you turn it on. If you turn on cloud clean-up, the text of each dictation and the words in your dictionary leave this PC and go to the service you choose, to be tidied; your voice still stays here unless you also turn on cloud speech. " +
        "Most people do not need it: Tidy my words runs on this PC.",
        "Before Dictum sends text to a service off this PC, it shows a warning. For Anthropic, it says: \"" + CloudPolicy.ConsentWarning(CloudExampleHost) + "\"",
        "Dictum can use Anthropic, OpenAI, Google Gemini or your own server. For Anthropic, OpenAI and Google Gemini, you need your own account with that service and an API key.",
        "To get a key, sign in to the service's website and create an API key. The key is like a password: keep it to yourself.",
        "1. Open Settings > Clean-up. Under Cloud clean-up, choose your service in the Service list: Anthropic (Claude), OpenAI, Google Gemini, Ollama on this PC, or Custom server (OpenAI-compatible) for your own server. " +
        "For your own server, also type its address in Server address and its model name in Model. The line under Model says which name its key needs.",
        "2. Click Open the settings folder. File Explorer opens Dictum's settings folder.",
        "3. Press Ctrl+L. The folder's address shows, highlighted, at the top of that window.",
        "4. Press Ctrl+C to copy the folder's address.",
        "5. Open Notepad: click the Start button, type Notepad, then press Enter.",
        "6. Type one line: " + SecretStore.AnthropicKey + "=your-key-here, with your own key in place of your-key-here and no spaces. " +
        "For OpenAI, the line starts " + SecretStore.OpenAiKey + "=; for Google Gemini, " + SecretStore.GeminiKey + "=; for your own server, if it needs a key, " + SecretStore.LlmKey + "=. " +
        "If you use more than one service, put each on its own line.",
        "7. In Notepad, click File.",
        "8. Click Save as.",
        "9. Open the Save as type list.",
        "10. Choose All files.",
        "11. Click in File name.",
        "12. Press Ctrl+A to select what is there.",
        "13. Press Ctrl+V to paste the folder's address.",
        "14. Type \\.env straight after it.",
        "15. Click Save.",
        "The file must be called .env exactly: a dot, then env, with nothing after it. A file called .env.txt is not read. In the folder, the Type column beside .env says ENV File, not Text Document.",
        "16. Go back to Settings > Clean-up and click Use cloud clean-up… to switch it on. For a service off this PC, Dictum then says what it will send and to whom, with the warning above. Read it. " +
        "Click Allow cloud clean-up only if you agree. Nothing is sent until you do.",
        "When Dictum has found your key, the Cloud clean-up card on that page starts with \"On: the text of each dictation is sent to\" and names the service, and the Dictum menu says \"" + CloudPolicy.TrayLine + "\". " +
        "Dictum cannot check that the key itself is right: if your dictations come back only lightly tidied, check the key.",
        "If the card says \"On, but waiting for your …\", Dictum cannot find your key. For OpenAI, for example, it says \"" + CloudPolicy.WaitingForKeyLine(SecretStore.OpenAiKey) + "\" " +
        "Check the file's name, and the line in it.",
        "To turn it off, open Settings > Clean-up and click Switch cloud clean-up off. Your dictations are tidied on this PC again.",
        "Dictum reads your key only from the .env file (or from a Windows environment variable). It never saves the key in its settings, and never shows it on screen or writes it in its log.",
    ];

    /// <summary>The questions, in order; each answer is one or more paragraphs.</summary>
    public static readonly IReadOnlyList<HelpEntry> Questions =
    [
        new("Where does my voice go?",
        [
            // APPROVED 22/10/2026 17:45 AEDT (qualifier added; proposed 21/10/2026): do not reword.
            "Nowhere. Dictum turns your voice into text on this PC, then throws the recording away. It never saves your voice to a file and never sends it anywhere unless you turn on cloud speech.",
            "The only recordings Dictum keeps are the ones you make on purpose in Calibrate my voice. They stay on this PC until you delete them.",
            // APPROVED 22/10/2026 17:22 AEDT (qualifier added; proposed 21/10/2026): do not reword.
            "If you turn on cloud clean-up (it is off unless you turn it on), the text of each dictation, and the words in your dictionary, are sent to the service you chose. Your voice still stays here unless you also turn on cloud speech.",
        ]),
        new("Why did it type the wrong words?",
        [
            "Dictum can mishear words, just as people can. These things help:",
            "• Keep the microphone close to your mouth, and speak at your normal speed.",
            "• If a name or word keeps coming out wrong, add it with Dictionary in the Dictum menu.",
            "• To teach Dictum a fix, choose Fix last dictation in the Dictum menu. Correct the text, tick the changes you want kept, then choose Save corrections. Dictum makes that fix from then on.",
            "• For better accuracy overall, see \"Why Dictum hears some people better than others\" above.",
        ]),
        new("It says \"Mic error\" or \"" + NoSoundText.Pill + "\", or nothing happens when I talk. What do I check?",
        [
            // APPROVED 22/10/2026 (proposed 21/10/2026): the two paragraphs reuse NoSoundText's wording, so the help and the message cannot drift apart.
            "If Dictum shows \"" + NoSoundText.Pill + "\", it says: " + NoSoundText.SilentBalloon,
            "If Dictum cannot find a microphone at all, it says: " + NoSoundText.NoMicrophoneBalloon,
            "• Check the microphone is plugged in, switched on and not muted. Many headsets have a mute button.",
            "• Open Settings > Microphone. Choose your microphone from the list, then choose Test microphone and speak. The bars move when Dictum hears you.",
            "• In Windows Settings, go to Privacy & security > Microphone. \"Microphone access\" and \"Let desktop apps access your microphone\" must both be on.",
            "• On a remote or virtual desktop (a computer you use over the internet or your office network), your own microphone has to be passed through to it. " +
            "This is called microphone redirection, and it must be turned on. If you are not sure how, ask whoever looks after your computers.",
        ]),
        new("Dictum closed straight away when I opened it. What do I do?",
        [
            // APPROVED 22/10/2026 (proposed 21/10/2026): the first sentence is the 1.1.2 note's wording (docs/STORE.md, "CHANGED SINCE THE LAST SUBMISSION"), word for word.
            CrashFixLine + " If Dictum closes on another PC, install the latest version from the Microsoft Store or from dictum.kynni.au.",
            "If the Dictum menu, or Settings > About, shows a button called Try on the CPU, choose it. Dictum then runs the speech model on the computer's main chip (the CPU) instead of the graphics card. This can be slower.",
            "If it still closes, email " + SupportMail.Address + " and tell us.",
        ]),
        new("How do I change the key I hold?",
        [
            "Open Settings > General. Under Press your hotkey (the hotkey is the key you hold), choose Change…, then press the key you want. When Dictum shows its name, choose Use this hotkey.",
            "Most keys work, and so do the side buttons on a mouse. The Fn key, Esc and Tab cannot be used, and neither can Enter or Space on their own.",
        ]),
        new("How do I stop it listening?",
        [
            "Let go of the key. Dictum stops listening and types what you said.",
            "To cancel instead, press Esc before your words appear, while you are holding the key or just after you let go. Nothing is typed. The program you are typing in also receives the Esc.",
            "If you started hands-free (by tapping the key twice quickly), tap the key once more to stop. Esc also stops hands-free, and what is already typed stays.",
            "To turn Dictum off for a while, use the on/off switch at the top right of the Dictum menu.",
        ]),
        new("Can I use it in Word, Outlook, Teams or a web browser?",
        [
            "Yes. Dictum types into almost any program where you can type, including Word, Outlook, Teams and web browsers. Click where the words should go first.",
            "The exception is a program running as administrator (with extra permissions): Windows does not let Dictum type into it.",
        ]),
        new("Where are the model files, and how big are they?",
        [
            "Settings > Models shows the Model folder, where Dictum keeps the models you download. The speech model that comes with Dictum is about 150 MB (megabytes) and is kept with the program itself.",
            // APPROVED 22/10/2026 (proposed 21/10/2026, final round on the Simple view): names the choices as Settings > Models shows them.
            "Bigger speech models (More accurate and Most accurate) and AI clean-up (Add AI clean-up, shown under Tidy my words while the rule-based tidy is in use and the text is English) are downloads you choose. " +
            "If you add the largest, most accurate ones, they can take up several GB (gigabytes).",
            "To keep them on another drive, choose Browse… beside Model folder in Settings > Models. Settings > Storage lists every model with its size, and you can delete the ones you do not need there.",
        ]),
        // APPROVED 22/10/2026 (proposed 22/10/2026): do not reword. The quoted lines are APPROVED copy taken from
        // CloudPolicy (never reworded here) and the tray panel's line, pinned against the app by HelpPageTests.
        new(CloudQuestion, CloudAnswer),
        new("How do I remove Dictum completely?",
        [
            "Open Settings > Storage. At the bottom, choose Remove Dictum completely…",
            "Dictum first lists everything it will delete, with sizes. Then it asks you to type DELETE to confirm.",
            "It deletes its models, recordings, logs, settings, dictionary, corrections, snippets and saved keys, its Start menu shortcut and its start-with-Windows entry, then closes. " +
            "If you got Dictum from the Microsoft Store, Windows Settings then opens so you can uninstall the app itself; otherwise Dictum removes the program too, unless its first warning says it cannot.",
        ]),
        new("Is Dictum free?",
        [
            // FreeLine and FreeLaterNote are APPROVED verbatim (owner, 21/10/2026 22:18 AEDT): do not reword. The third paragraph is APPROVED 22/10/2026 (proposed 21/10/2026).
            FreeLine,
            FreeLaterNote,
            // The earlier answer's facts, kept: its first sentence is reworded so it does not repeat the two paragraphs above (APPROVED 22/10/2026; proposed 21/10/2026).
            "There is no charge and no word limit. Dictum is open source, which means anyone can read the program's code. It is made by Kynni Pty Ltd and shared under the MIT licence.",
        ]),
        new("Where is the full guide?",
        [
            "The full guide is on the Dictum website at dictum.kynni.au/help. Choose Open the full guide at the top of this page.",
            "It opens in your web browser and needs the internet. Everything on this page works without the internet.",
        ]),
        new(NameQuestion, [NameLine]),
    ];

    /// <summary>APPROVED 22/10/2026 (proposed 21/10/2026): the question that carries the approved name line.</summary>
    public const string NameQuestion = "What does the name Dictum mean?";

    /// <summary>APPROVED verbatim (owner, 21/10/2026 22:21 AEDT): do not reword.</summary>
    public const string NameLine = "The name: a dictum is a saying, something stated. From the Latin dictum, “something said”.";

    /// <summary>Every sentence on the page, headings and button labels included: what the copy pins read.</summary>
    public static IEnumerable<string> AllText() =>
    [
        PageTitle, IndexHeading, MoreHelpHeading, GuideNote, GuideButton, SupportNote, SupportButton, FeedbackNote, FeedbackButton,
        HowItWorksHeading, .. HowItWorks, AccuracyHeading, .. Accuracy,
        QuestionsHeading, .. Questions.SelectMany(q => (IEnumerable<string>)[q.Question, .. q.Answer]),
    ];
}

/// <summary>One question on the Help page and its answer, one entry per paragraph.</summary>
public sealed record HelpEntry(string Question, IReadOnlyList<string> Answer);
