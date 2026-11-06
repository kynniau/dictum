namespace Dictum.UI.Views.Meetings;

internal static class MeetingsText
{
    public const string PROPOSED_MeetingRecording = "Meeting: recording";
    public const string PROPOSED_MeetingPaused = "Meeting: paused";
    public const string PROPOSED_DictationBusy = "Finish dictation before starting a meeting.";
    public const string PROPOSED_HeadphonesTip = "Tip: wear headphones so the other people's voices are not picked up twice by your microphone.";
    public const string ApprovedScriptVersion = "APPROVED-2026-11-05-0913-AEDT-v1";
    public const string PROPOSED_UnconfiguredProvider = "the summary service (configure its host in Settings > Clean-up)";
    public const string PROPOSED_SummaryHost = "Summary service host: {0}";
    // simplification: retire when audio is saved in encrypted chunks during recording
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string PROPOSED_AudioRecovered = "Recording ended unexpectedly. Saved audio and transcript are available.";
    // Retired by chunked saving; kept unchanged for the approved-copy record.
    public const string APPROVED_AudioUntilStop = "Audio is held in memory until you press Stop. If Dictum stops unexpectedly or the computer shuts down before then, the audio may be lost. Only transcript text already saved will remain.";
    public const string PROPOSED_AudioSaveFailed = "The recording could not be saved. Your audio is still in memory. Retry saving or discard it.";
    public const string PROPOSED_Retry = "Retry";
    public const string PROPOSED_TranscriptionFailed = "Transcription stopped unexpectedly. Capture has stopped. Save your audio and stop the meeting.";
    public const string PROPOSED_SaveAudioStop = "Save audio and Stop";
    public const string PROPOSED_Meetings = "Meetings";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Start = "I confirm that I explained this and everyone agreed. Start recording";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Import = "I confirm that everyone agreed. Import";
    public const string PROPOSED_Search = "Search meetings";
    public const string PROPOSED_Empty = "No meetings found.";
    public const string PROPOSED_Open = "Open meeting";
    public const string PROPOSED_ListRow = "{0} · {1:g} · {2:hh\\:mm\\:ss} · {3:N0} bytes";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Cancel = "Cancel";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Before = "Before you record";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_BeforeImport = "Before you import";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Agreement = "Tell everyone what you plan to record and how you will use it. Explain the four points below. Ask each person if they agree. Do not record unless everyone clearly agrees.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_ImportAgreement = "Only import this recording if everyone recorded agreed to the recording and the uses shown here.\n\nCheck the speech service, summary service, access and deletion settings below. Explain any cloud processing or overseas staff access before asking for agreement.\n\nNothing is imported until you press the button. Dictum saves your confirmation and its time. This is your note, not proof that anyone agreed. The original file is not changed.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Local = "The summary is written on this computer.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_LocalSpeech = "The transcript is written on this computer.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Ollama = "Ollama makes the summary. A local model works on this computer. A cloud model sends transcript text to Ollama’s cloud service. That service may process or store it overseas. Its terms and your account settings affect access, use and retention. Explain this to everyone before asking them to agree.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_OwnKey = "Cloud summary is on. Dictum sends transcript text to {0} to make the summary. The service may process or store it overseas. Its terms and your account settings affect access, use and retention. Explain this to everyone before asking them to agree.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_CloudSpeech = "Cloud speech is on. Dictum sends recorded sound to {0} to make the transcript. The service may process or store it overseas. Its terms and your account settings affect access, use and retention. Explain this to everyone before asking them to agree.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_KeepForever = "How long Dictum keeps it: there is no automatic deletion. Saved recordings, transcripts and summaries stay until you delete them. Cloud services and copies outside Dictum may have different rules.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_KeepDays = "How long Dictum keeps it: automatic deletion is set to remove the saved recording after {0} days. The transcript and summary stay until you delete them. This does not delete copies held elsewhere.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_How = "How did everyone agree?";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Spoken = "Each person said yes";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Written = "Each person agreed in writing";
    public const string PROPOSED_Platform = "The meeting app announced the recording";
    public const string PROPOSED_Start = "Start a meeting";
    public const string PROPOSED_Import = "Import a recording";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Joined = "Someone has joined. Stop recording before asking them to agree. Explain what is recorded, how it is used, who can access it and how long it is kept. Start recording again only after they clearly agree. Your answer and its time are saved as your note, not proof of their agreement.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Continue = "I asked and they agreed. Resume recording";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Refused = "Someone said no";
    public const string PROPOSED_Notes = "Take notes instead";
    public const string PROPOSED_NotesText = "Nothing is being recorded. You can write notes for this meeting instead.";
    public const string PROPOSED_SaveNotes = "Save notes";
    public const string PROPOSED_Marker = "{0:hh\\:mm\\:ss} · {1}";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_MicOnly = "Only sound picked up by your microphone is recorded. Sound playing on this computer is not recorded directly.";
    public const string PROPOSED_Recording = "Recording";
    public const string PROPOSED_Paused = "Paused";
    public const string PROPOSED_Elapsed = "Elapsed time";
    public const string PROPOSED_You = "You (microphone)";
    public const string PROPOSED_Live = "Live transcript";
    public const string PROPOSED_Pause = "Pause";
    public const string PROPOSED_Resume = "Resume";
    public const string PROPOSED_Stop = "Stop";
    public const string PROPOSED_Discard = "Stop and discard";
    public const string PROPOSED_Mark = "Mark this moment";
    public const string PROPOSED_Joined = "Someone joined";
    public const string PROPOSED_Marked = "Moment marked.";
    public const string PROPOSED_Transcript = "Transcript";
    public const string PROPOSED_Summary = "Summary";
    public const string PROPOSED_Template = "Summary template";
    public const string PROPOSED_Regenerate = "Regenerate";
    public const string PROPOSED_NoSummary = "No summary written yet.";
    public const string PROPOSED_Export = "Export Markdown";
    public const string PROPOSED_Copy = "Copy";
    public const string PROPOSED_Delete = "Delete";
    public const string PROPOSED_DeleteQuestion = "Delete the recording, transcript and summary from this computer? This cannot be undone. The original imported file stays where it is.";
    public const string PROPOSED_Busy = "Working…";
    public const string PROPOSED_Error = "That did not work. Check your Meetings settings and that the models are ready, then try again.";
    public const string PROPOSED_ConfigureSummary = "Choose and agree to this summary service under Settings > Clean-up first.";
    public const string PROPOSED_Copied = "Copied.";
    public const string PROPOSED_Saved = "Saved.";
    public const string PROPOSED_Status = "Meeting status";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Record = "{0}. At {1:g}, you said everyone agreed: {2}. This is your note. It does not prove that anyone agreed.";
    public const string PROPOSED_Record = "Meeting record";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_LiveKind = "Live recording";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_ImportKind = "Import";
    public const string PROPOSED_Segment = "{0:hh\\:mm\\:ss} · {1}: {2}";
    public const string PROPOSED_YouChannel = "You";
    public const string PROPOSED_OthersChannel = "Others";
    public const string PROPOSED_MixedChannel = "Mixed";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Enable = "Meetings saves recordings you start or import, along with transcripts and summaries. Dictum encrypts saved meeting content. Keep a safe export before moving to another computer. Check your audio-saving and deletion settings.";
    public const string PROPOSED_Enable = "Turn on Meetings";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Protection = "It does not protect against harmful software running while you\nare signed in.";
    public const string PROPOSED_Folder = "Meetings folder";
    public const string PROPOSED_FolderNote = "Close and reopen Meetings to use a changed folder. Existing meetings stay where they were saved.";
    public const string PROPOSED_Change = "Change";
    public const string PROPOSED_Retention = "Keep audio";
    public const string PROPOSED_Forever = "Until I delete it";
    public const string PROPOSED_Days30 = "30 days";
    public const string PROPOSED_Days90 = "90 days";
    public const string PROPOSED_Engine = "Summary engine";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_LocalEngine = "On this computer (default)";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_OllamaEngine = "Ollama — local or cloud model";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_OwnKeyEngine = "My own Claude or OpenAI key (transcript text leaves this PC)";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_DriveWarning = "Dictum has not confirmed that this drive has whole-drive encryption. Dictum encrypts saved meeting content, but other files may be readable if someone takes the drive. Check the drive’s encryption settings in Windows.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Dismiss = "Dismiss on this computer";
    public const string PROPOSED_Existing = "Open existing meetings";
    public const string PROPOSED_CloseRecording = "Stop the recording before closing this window.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Acknowledged = "At {0:g}, you said you explained this and everyone agreed. This is your note, not proof of their agreement.";
    public const string PROPOSED_General = "General meeting";
    public const string PROPOSED_Client = "Client meeting file note";
    public const string PROPOSED_Lender = "Lender or supplier call";
    public const string PROPOSED_Standup = "Stand-up";
    public static readonly string[] PROPOSED_TemplateNames = [PROPOSED_General, PROPOSED_Client, PROPOSED_Lender, PROPOSED_Standup];
    public static readonly string[] TemplateIds = ["general", "client-file-note", "lender-call", "stand-up"];
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_CaptureMicrophone = "What is recorded: sound picked up by this computer’s microphone. This may include nearby voices and sounds.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_CaptureSystem = "What is recorded: sound picked up by this computer’s microphone and sound playing on this computer.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Uses = "What happens to it: Dictum uses the sound to make a transcript, which is a written copy of the speech. It also makes a summary. Both can contain mistakes.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Access = "Who can access it: people who can use your signed-in computer may be able to open your meetings. Cloud services receive the sound or text described below. If you share, export or copy meeting content, other people may receive it. Staff who can access it may be overseas.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_AudioOff = "Dictum does not keep an audio recording after processing. The transcript and summary stay until you delete them. Cloud services and copies outside Dictum may have different rules.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_AgreementOptions = "Each person said yes\nEach person agreed in writing";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_ConfirmationNote = "Pressing the button saves the time and your answer. This records what you say you explained and how you say everyone agreed. It does not prove that anyone agreed.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_KindLabels = "Live recording\nImport";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_RefusalNotes = "Recording has stopped. You can type notes instead. Choose Stop and discard to delete this meeting’s saved files.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_EnableImportsTitle = "Before you turn on imports";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_EnableImportsBody = "Imports are off until you turn them on.\n\nAn imported recording was made outside Dictum. Dictum cannot check whether everyone agreed to the recording or its use here.\n\nCheck that you are allowed to keep, use and share it before you import. Everyone recorded must have agreed to the recording and the uses you explain.\n\nDictum saves a copy. It uses the recording to make a transcript and summary. Your speech and summary settings decide whether sound or text goes to a cloud service. Those services may process or store it overseas.\n\nCheck who can access the meeting and how long Dictum will keep it. Staff who can access it may be overseas.\n\nDictum encrypts the saved meeting copy. The original file stays where it is and is not changed.\n\nFor each import, Dictum saves your confirmation and its time. This is your note, not proof that anyone agreed.\n\nThis notice does not tell you whether an import is lawful.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_EnableImports = "I understand. Turn on imports";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_ShareReminder = "You are about to copy, export or share meeting content.\n\nPeople who receive it may save it or pass it on. Copies outside Dictum may not have Dictum’s protection. Staff who can access shared content may be overseas.\n\nCheck that you are allowed to share it. Ask everyone recorded to agree to what you will share and who will receive it. Share only what those people need.\n\nAgreement to recording does not automatically include agreement to sharing. This reminder does not tell you whether sharing is lawful.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_ShareContinue = "I understand. Continue";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_TeamSharing = "Team sharing is off until you turn it on. People with access to the team folder may be able to open what you share. Staff with access may be overseas. Dictum records each share and its time. This does not prove that anyone agreed.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_Disclaimer = "Meetings does not tell you whether recording or sharing is lawful.\n\nRules can differ between places. Check the rules that apply to you and the other people involved.\n\nDictum requires everyone’s clear agreement before recording or importing. This does not guarantee that recording, importing or sharing is lawful. Your button press records your statement. It does not prove that anyone else agreed.\n\nDictum is free, open-source software. Read its licence for its terms.\n\nThis is not legal advice.";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_EnableConfirm = "I understand. Turn on Meetings";
    // APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed by a lawyer): do not reword.
    public const string APPROVED_EngineNote = "On this computer: Dictum makes the summary here. It does not send the transcript to a cloud summary service.\n\nOllama: a local model makes the summary on this computer. A cloud model sends transcript text to Ollama’s cloud service. Check which model you selected.\n\nClaude or OpenAI: Dictum sends transcript text to your chosen service to make the summary.\n\nCloud speech is a separate setting. If it is on, Dictum sends recorded sound to your chosen speech service.\n\nCloud services may process or store sound or text overseas. Their terms and your account settings affect access, use and retention. Dictum cannot promise how they protect or delete it.\n\nCopying, exporting and team sharing can also send meeting content elsewhere.";
}
