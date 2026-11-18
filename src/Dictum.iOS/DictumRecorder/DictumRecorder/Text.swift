// Every user-facing string in Dictum Recorder lives in this file.
//
// APPROVED_ constants are copied character for character from the approved desktop copy in
// src/Dictum.UI/Views/Meetings/MeetingsText.cs (marked there "APPROVED 05/11/2026 09:13 AEDT (owner; not reviewed
// by a lawyer): do not reword"). Never reword them here; change them only by copying a newer approved desktop text.
//
// PROPOSED_ constants are new for the phone and are proposals awaiting approval.

import Foundation

// MARK: - Screen 1: Before you record (consent)

let APPROVED_Before = "Before you record"
let APPROVED_Agreement = "Tell everyone what you plan to record and how you will use it. Explain the four points below. Ask each person if they agree. Do not record unless everyone clearly agrees."

let PROPOSED_PointRecord = "This phone records the sound in the room and encrypts it as it records. No sound is stored in the clear."
let PROPOSED_PointTransfer = "The encrypted file is moved to your computer by you. Nothing is sent anywhere by this app."
let PROPOSED_PointProcessing = "Transcripts and summaries are made on the computer that imports the file, under its own notice."
let PROPOSED_PointKeep = "The encrypted file stays on this phone until you delete it. Cloud copies you make have their own rules."

/// The four phone points, in the order shown on screen 1.
let PROPOSED_Points = [PROPOSED_PointRecord, PROPOSED_PointTransfer, PROPOSED_PointProcessing, PROPOSED_PointKeep]

let APPROVED_How = "How did everyone agree?"
let APPROVED_Spoken = "Each person said yes"
let APPROVED_Written = "Each person agreed in writing"
let APPROVED_AgreementOptions = "Each person said yes\nEach person agreed in writing"

let PROPOSED_TitleLabel = "Meeting title (optional)"
/// Stored as the meeting title when the title field is left empty.
let PROPOSED_DefaultTitle = "Meeting"

let APPROVED_Start = "I confirm that I explained this and everyone agreed. Start recording"

let PROPOSED_MicDenied = "Dictum Recorder needs the microphone to record. Allow it in Settings."
let PROPOSED_OpenSettings = "Open Settings"

/// The exact text shown on screen 1, saved in the bundle's confirmation as the script that was shown.
let PROPOSED_ConsentScript = APPROVED_Agreement + "\n\n" + PROPOSED_Points.joined(separator: "\n")
let PROPOSED_ScriptVersion = "PROPOSED-ios-2026-11-08-v1"

// With "Keep meetings on this device" on (17/11/2026), points 3 and 4 say what is kept, and the script version changes.
// The points and version above stay for keep-off.
let PROPOSED_PointProcessingKept = "Transcripts and summaries can be made on this device and are kept on it, encrypted, until you delete them. A computer that imports the file works under its own notice."
let PROPOSED_PointKeepKept = "The recording and its notes stay on this device, encrypted and opened with Face ID or the passcode, until you delete them. Cloud copies you make have their own rules."
let PROPOSED_PointsKept = [PROPOSED_PointRecord, PROPOSED_PointTransfer, PROPOSED_PointProcessingKept, PROPOSED_PointKeepKept]
let PROPOSED_ConsentScriptKept = APPROVED_Agreement + "\n\n" + PROPOSED_PointsKept.joined(separator: "\n")
let PROPOSED_ScriptVersionKept = "PROPOSED-ios-2026-11-17-v2"

/// The points shown, and the script and version recorded, for the keep setting in force when Start is tapped.
func PROPOSED_PointsFor(keep: Bool) -> [String] { keep ? PROPOSED_PointsKept : PROPOSED_Points }
func PROPOSED_ConsentScriptFor(keep: Bool) -> String { keep ? PROPOSED_ConsentScriptKept : PROPOSED_ConsentScript }
func PROPOSED_ScriptVersionFor(keep: Bool) -> String { keep ? PROPOSED_ScriptVersionKept : PROPOSED_ScriptVersion }

// MARK: - Screen 2: Recording

let PROPOSED_RecordingIndicator = "Recording"
let PROPOSED_ElapsedLabel = "Elapsed time"
let PROPOSED_InputLevel = "Input level"
/// "{0} percent" for VoiceOver on the input level bar.
let PROPOSED_InputLevelValue = "%d percent"
let PROPOSED_NoSoundDetected = "No sound is reaching the microphone. Check the phone is not being controlled from another device and that the microphone is allowed."
let PROPOSED_Stop = "Stop and seal"
let PROPOSED_Sealing = "Sealing…"
/// Shown only when the system ended an interruption (for example a phone call) without allowing capture to resume.
let PROPOSED_Interrupted = "Paused: another app or a call stopped the microphone. Nothing is being captured."
let PROPOSED_Resume = "Resume recording"

// MARK: - Screen 3: Sealed

let PROPOSED_SealedHeading = "Sealed"
let PROPOSED_FileLabel = "File"
let PROPOSED_SizeLabel = "Size"
let PROPOSED_CodeLabel = "Transfer code"
let PROPOSED_CodeHint = "Read these six words into Dictum on your computer when you import the file. They are not saved anywhere. Write them down now if you need them later."
let PROPOSED_Share = "Send the file"
let PROPOSED_Done = "Done"
let PROPOSED_FilesHint = "The file is also in Files → On My iPhone → Dictum Recorder."
let PROPOSED_FilesHintPad = "The file is also in Files → On My iPad → Dictum Recorder."
let PROPOSED_IncompleteNote = "Cut short: Dictum will show this when it is imported."

// MARK: - Screen 4: Recovered

let PROPOSED_RecoverHeading = "A recording was cut short"
let PROPOSED_RecoverBody = "The app closed before the recording was sealed. The part that was captured is encrypted and can still be sealed and imported."
let PROPOSED_RecoverSeal = "Seal what was captured"
let PROPOSED_RecoverNothing = "Nothing was captured before the app closed, so there is nothing to seal."
let PROPOSED_RecoverNoKey = "This recording cannot be sealed because its key is no longer on this phone."

// MARK: - Errors

let PROPOSED_FailedHeading = "Something went wrong"
let PROPOSED_StartFailed = "Recording could not start. Nothing was captured."
let PROPOSED_SealFailed = "The recording could not be sealed. The encrypted part stays on this phone and will be offered for sealing next time the app opens."
let PROPOSED_CaptureFailed = "Capture stopped early because the encrypted file could not be written. What was captured before then has been sealed."
let PROPOSED_NoMicrophone = "No microphone is available on this device."
let PROPOSED_BackToStart = "Back to start"

// MARK: - Experimental on-device transcript (switch on screen 1, default off)

let PROPOSED_OnDeviceTranscriptToggle = "Experimental: transcribe on this device"
let PROPOSED_OnDeviceTranscriptNote = "Experimental. The words appear on this device only while you record and until you tap Done. They are not saved and are not sent anywhere. The computer that imports the file makes the real transcript."
let PROPOSED_OnDeviceTranscriptNoteKept = "Experimental. The words are made on this device and kept with the meeting on it, encrypted, until you delete the meeting. They are not sent anywhere. The computer that imports the file makes its own transcript."
let PROPOSED_TranscriptChecking = "Checking for the on-device speech model…"
let PROPOSED_TranscriptAssetsNeeded = "The on-device speech model is not on this device yet. Downloading it from Apple is a one-off and needs the internet."
let PROPOSED_TranscriptDownload = "Download the speech model"
let PROPOSED_TranscriptDownloading = "Downloading the speech model…"
let PROPOSED_TranscriptReady = "The on-device speech model is on this device."
let PROPOSED_TranscriptNotInstalled = "The speech model did not finish installing."
/// "{0}" is the system's error description.
let PROPOSED_TranscriptDownloadFailed = "The speech model could not be downloaded: %@"
let PROPOSED_StartNeedsModel = "To start, download the speech model or turn off the experimental switch."

let PROPOSED_TranscriptPreparing = "Preparing the on-device speech model…"
let PROPOSED_TranscriptListening = "On-device transcript (experimental)"
let PROPOSED_TranscriptUnavailable = "On-device transcript is not available on this device."
/// "{0}" is the system's error description.
let PROPOSED_TranscriptFailed = "On-device transcript stopped: %@"
let PROPOSED_TranscriptRegion = "On-device transcript so far"
let PROPOSED_TranscriptEmpty = "No words yet."

let PROPOSED_TranscriptHeading = "On-device transcript (experimental, not saved)"
let PROPOSED_TranscriptHeadingKept = "On-device transcript (experimental, kept on this device, encrypted)"
let PROPOSED_TranscriptFinishing = "Finishing the on-device transcript…"
let PROPOSED_TranscriptNone = "No words were recognised."
let PROPOSED_CopyTranscript = "Copy transcript"
let PROPOSED_TranscriptCopied = "Copied"

let PROPOSED_FactsHeading = "Experiment facts"
let PROPOSED_FactLocale = "Language used"
let PROPOSED_FactAssets = "Speech model"
let PROPOSED_FactAssetsPreinstalled = "Already on this device (no download)"
/// "{0}" is seconds, one decimal place.
let PROPOSED_FactAssetsInstalled = "Installed on request in %.1f seconds (network event to Apple)"
/// "{0}" is the status before: supported, downloading, installed or unsupported.
let PROPOSED_FactAssetsNoRequest = "No installation request was needed (status before: %@)"
let PROPOSED_FactAssetStatusBefore = "Model status before Start"
let PROPOSED_FactStopToFinal = "Stop to last final words"
/// "{0}" is seconds, one decimal place.
let PROPOSED_FactSeconds = "%.1f seconds"
let PROPOSED_FactNoFinalAfterStop = "No final words arrived after Stop"
let PROPOSED_FactThermal = "Thermal state at Stop"
let PROPOSED_FactOffline = "Transcribed with networking off"
/// Shown when no network path was ever available between Start and Stop.
let PROPOSED_FactYes = "Yes"
let PROPOSED_FactNo = "No (a network was available)"

/// Copied by hand into Info.plist as NSSpeechRecognitionUsageDescription (a plist cannot read this constant).
let PROPOSED_SpeechUsage = "Dictum Recorder can show an experimental on-device transcript while you record. Speech is processed on this device only."

// MARK: - Experimental speaker identification (with the on-device transcript switch)

/// Copied from the approved desktop copy in src/Dictum.Core/Meetings/Speakers/MeetingSpeakers.cs (SpeakerText, "APPROVED
/// 08/11/2026 (owner, round 2): do not reword"): "Other {0}", with the placeholder written as %d. "%d" is the speaker
/// number, 1 for the first voice heard.
let APPROVED_Other = "Other %d"

let PROPOSED_SpeakersWorking = "Identifying speakers (experimental)…"
let PROPOSED_SpeakersUnavailable = "Speaker identification needs the speaker model (not in this build)."
let PROPOSED_SpeakersTooLittle = "Too little speech to tell the voices apart."
/// "{0}" is the system's error description.
let PROPOSED_SpeakersFailed = "Speaker identification stopped: %@"
/// "{0} percent" for VoiceOver on the speaker progress bar.
let PROPOSED_SpeakersProgressValue = "%d percent"
let PROPOSED_SpeakerLinesRegion = "On-device transcript by speaker"

let PROPOSED_FactSegments = "Transcript segments"
let PROPOSED_FactSegmentTimes = "Segment times from"
let PROPOSED_FactTimesWords = "Word time ranges"
let PROPOSED_FactTimesResults = "Result time ranges"
let PROPOSED_FactTimesWhole = "No time ranges: one segment for the whole recording"
let PROPOSED_FactWindows = "Voice windows"
/// "{0}" windows cut from the segments, "{1}" of them with a voice print.
let PROPOSED_FactWindowsValue = "%d cut, %d with a voice print"
let PROPOSED_FactEmbedding = "Voice print time"
/// "{0}" is seconds, "{1}" seconds per minute of audio, one decimal place each.
let PROPOSED_FactEmbeddingValue = "%.1f seconds (%.1f seconds per minute of audio)"
let PROPOSED_FactClustering = "Clustering time"
let PROPOSED_FactSpeakers = "Speakers found"
let PROPOSED_FactThreshold = "Separation threshold"
/// "{0}" is the cosine-distance threshold, two decimal places.
let PROPOSED_FactThresholdValue = "%.2f"
let PROPOSED_FactWindowRule = "Window rule"
/// "{0}" window seconds, "{1}" seconds between window starts, "{2}" the shortest segment given a window.
let PROPOSED_FactWindowRuleValue = "%.1f s every %.1f s, none under %.1f s"
let PROPOSED_FactLinkage = "Linkage"
let PROPOSED_FactLinkageComplete = "Complete"
let PROPOSED_FactLinkageAverage = "Average (UPGMA)"
let PROPOSED_FactSegmentMean = "One voice print per segment"
let PROPOSED_FactClustered = "Vectors clustered"
let PROPOSED_FactDesktopRule = "Same as the desktop"
let PROPOSED_FactSegmentVectors = "Segment vectors"
let PROPOSED_FactDistanceP50 = "Distance between vectors, median"
let PROPOSED_FactDistanceP90 = "Distance between vectors, 90th percentile"
let PROPOSED_FactDistanceMax = "Distance between vectors, largest"
let PROPOSED_FactSmallestBetween = "Smallest distance between clusters at the cut"
let PROPOSED_FactLargestMerge = "Largest merge applied"
/// "{0}" is a cosine distance, three decimal places.
let PROPOSED_FactDistanceValue = "%.3f"
/// VoiceOver before a line's words: "{0}" is the mm:ss shown before the line.
let PROPOSED_LineAt = "at %@"
let PROPOSED_SpeakersExperimentalNote = "Experimental: speaker labels are a test of the method, not a result. Compare the count with who was actually speaking."
let PROPOSED_FactPcm = "Sound kept in memory"
/// "{0}" is megabytes (1,000,000 bytes), one decimal place.
let PROPOSED_FactMegabytes = "%.1f MB"
let PROPOSED_FactCap = "60-minute memory limit reached"
let PROPOSED_FactCapNo = "No"
let PROPOSED_FactNotRun = "Not run"

// MARK: - Brand and home screen (proof of concept)

let PROPOSED_BrandName = "Dictum"
let PROPOSED_RecorderTag = "Recorder · beta"
let PROPOSED_ProofOfConcept = "Proof of concept, not a released product"
let PROPOSED_NewMeeting = "New meeting"
let PROPOSED_SealedRecordings = "Sealed recordings"
let PROPOSED_NoRecordings = "No sealed recordings yet. Each meeting you record is sealed here until you send or delete it."
let PROPOSED_SettingsButton = "Settings"
let PROPOSED_Back = "Back"
/// VoiceOver for a sealed file row: "{0}" the file name, "{1}" the date, "{2}" the size.
let PROPOSED_RecordingRow = "%@, %@, %@"
let PROPOSED_ShareRecording = "Send this file"
let PROPOSED_DeleteRecording = "Delete this file"
/// Confirmation title: "{0}" is the file name.
let PROPOSED_DeleteConfirmTitle = "Delete %@?"
let PROPOSED_DeleteConfirmMessage = "This deletes the sealed file from this device. It cannot be undone. Copies you have already sent are not affected."
let PROPOSED_DeleteConfirm = "Delete the file"
let PROPOSED_Cancel = "Cancel"
let PROPOSED_DeleteFailed = "The file could not be deleted."

// MARK: - Settings screen

let PROPOSED_SettingsTitle = "Settings"
let PROPOSED_SettingsDone = "Done"
let PROPOSED_SectionEngine = "Transcription engine"
let PROPOSED_EngineApple = "Apple on-device"
let PROPOSED_EngineAppleAvailable = "Available, runs offline"
let PROPOSED_EngineAppleUnavailable = "Not available on this device"
let PROPOSED_EngineWhisperBase = "whisper.cpp base.en"
let PROPOSED_EngineWhisperSmall = "whisper.cpp small.en"
let PROPOSED_EngineWhisperTurbo = "whisper.cpp large-v3-turbo"
let PROPOSED_NotYetMeasured = "Not yet measured"
let PROPOSED_Selected = "Selected"
let PROPOSED_SectionSpeakers = "Speaker labels"
let PROPOSED_SpeakerLabelsToggle = "Label speakers after the meeting"
let PROPOSED_ThresholdLabel = "Separation threshold"
let PROPOSED_ThresholdHint = "Higher joins more voices into one speaker; lower splits them apart."
let PROPOSED_SectionMeetingType = "Meeting type"
let PROPOSED_MeetingTypePicker = "Meeting type"
let PROPOSED_SectionSummary = "Summary"
let PROPOSED_SummaryApple = "On this device — Apple model"
let PROPOSED_SummaryAppleNotAvailable = "Not available on this device"
let PROPOSED_SummaryAppleAvailable = "Available, runs on this device"
let PROPOSED_SummaryOwnKey = "Own API key"
let PROPOSED_NotInThisBuild = "Not in this build"
let PROPOSED_SectionAppearance = "Appearance"
let PROPOSED_AppearanceAutomatic = "Automatic"
let PROPOSED_AppearanceLight = "Light"
let PROPOSED_AppearanceDark = "Dark"
let PROPOSED_SectionKeep = "Keep meetings on this device"
let PROPOSED_KeepToggle = "Keep meetings on this device"
let PROPOSED_KeepNote = "Saved meetings are encrypted on this device and open with Face ID or your passcode. Nothing is sent anywhere."
let PROPOSED_KeepOffNote = "Off: after sealing, the key is deleted and nothing about the meeting is kept on this device except the sealed file."
let PROPOSED_SectionAbout = "About"
let PROPOSED_AboutVersion = "Version"
let PROPOSED_AboutBuild = "Build"
let PROPOSED_BuildShaUnknown = "Not stamped"
let PROPOSED_AboutLicences = "Licences"
let PROPOSED_LicenceDictum = "Dictum Recorder: MIT licence, Kynni Pty Ltd"
let PROPOSED_LicenceSherpa = "sherpa-onnx: Apache License 2.0"
let PROPOSED_LicenceCampPlus = "3D-Speaker CAM++ speaker model: Apache License 2.0"
let PROPOSED_LicenceBip39 = "BIP-39 English word list (transfer code): MIT licence"

// Meeting type names. The same words as the desktop's built-in summary types (SummaryTemplates.Builtins in
// src/Dictum.Core/Meetings/Summary/SummaryTemplate.cs), which are not marked approved copy there, so PROPOSED_ here.
let PROPOSED_TypeGeneral = "General meeting"
let PROPOSED_TypeClientFileNote = "Client file note"
let PROPOSED_TypeLenderCall = "Lender call"
let PROPOSED_TypeStandUp = "Stand-up"

// MARK: - Speaker naming (copied from src/Dictum.Core/Meetings/Speakers/MeetingSpeakers.cs, SpeakerText, "APPROVED
// 08/11/2026 (owner, round 2): do not reword"; "{0}" written as %@ or %d)

let APPROVED_FewerSpeakers = "Fewer speakers"
let APPROVED_MoreSpeakers = "More speakers"
let APPROVED_RenameSpeaker = "Name for %@"
let APPROVED_SaveNames = "Save names"
let APPROVED_Limits = "Two similar voices on one line may be merged. Names apply to this meeting only."
let APPROVED_Separating = "Identifying speakers…"
let APPROVED_Separated = "Speakers identified: %d."
let APPROVED_InvalidName = "Use letters, numbers, spaces, full stops, hyphens and apostrophes in a name."

// MARK: - Review (end of meeting)

let PROPOSED_ReviewHeading = "Before everyone leaves"
let PROPOSED_ReviewIntro = "Check the transcript, name the speakers, agree the actions, then continue to the sealed file. Nothing on this screen is saved."
let PROPOSED_ReviewTranscript = "Transcript"
let PROPOSED_RenameHint = "Tap a speaker to give them a name for this meeting."
/// VoiceOver hint on a speaker chip.
let PROPOSED_RenameAction = "Name this speaker"
let PROPOSED_NameField = "Name"
let PROPOSED_ReviewSummary = "Summary"
let PROPOSED_MakeSummary = "Write the summary on this device"
let PROPOSED_MakeSummaryAgain = "Write the summary again"
let PROPOSED_SummaryWorking = "Writing the summary on this device…"
let PROPOSED_SummaryDone = "Summary written on this device"
let PROPOSED_SummaryUnavailable = "On-device summary is not available on this device."
/// "{0}" is the system's error description.
let PROPOSED_SummaryFailed = "The summary could not be written: %@"
let PROPOSED_SummaryTooLong = "The transcript is too long for the on-device model, even in parts."
let PROPOSED_SummaryNeedsTranscript = "There is no transcript to summarise."
let PROPOSED_SummaryWaitSpeakers = "Waiting for the speaker step to finish…"
let PROPOSED_SummaryExperimentalNote = "Experimental: a test of the method, not a result. Check every action against what was actually said."
let PROPOSED_SummaryFactsHeading = "Summary facts"
let PROPOSED_FactModelAvailable = "On-device model available"
let PROPOSED_FactModelReason = "Why not available"
let PROPOSED_ReasonDeviceNotEligible = "This device cannot run it"
let PROPOSED_ReasonNotEnabled = "Apple Intelligence is turned off"
let PROPOSED_ReasonNotReady = "The model is not ready (the system may still be downloading it)"
let PROPOSED_ReasonOther = "Other reason reported by the system"
let PROPOSED_FactModelStatusBefore = "Model status before the summary"
let PROPOSED_FactModelRequest = "Download requested by this app"
let PROPOSED_FactModelRequestNo = "No (the system manages the model; this app cannot request it)"
let PROPOSED_FactSummarySeconds = "Seconds to the summary"
let PROPOSED_FactSummaryParts = "Transcript parts summarised"
let PROPOSED_FactModelStatusAvailable = "available"
let PROPOSED_FactModelStatusUnavailable = "unavailable"
let PROPOSED_FactModelStatusNotChecked = "not checked (needs iOS 26 or later)"
let PROPOSED_ReviewActions = "Who does what"
let PROPOSED_ActionOwner = "Who"
let PROPOSED_ActionOwnerPick = "Choose who"
let PROPOSED_ActionText = "Action"
let PROPOSED_ActionByWhen = "By when (optional)"
let PROPOSED_AddAction = "Add an action"
let PROPOSED_RemoveAction = "Remove this action"
let PROPOSED_NoActions = "No actions yet. Add one, or write the summary to suggest some."
/// VoiceOver label for an action row: "{0}" is its number.
let PROPOSED_ActionRow = "Action %d"
let PROPOSED_ReviewAgreement = "Agreement"
let PROPOSED_AgreementText = "Read the summary and the actions aloud. Each person confirms their own actions."
let PROPOSED_EveryoneConfirmed = "Everyone confirmed"
/// "{0}" is the time, for example 4:05 pm.
let PROPOSED_ConfirmedAt = "Everyone confirmed at %@"
let PROPOSED_ConfirmedNote = "Recorded on this screen only for this proof of concept; nothing is saved."
let PROPOSED_ConfirmedNoteKept = "The confirmation time is saved with the meeting on this device, encrypted."
let PROPOSED_ReviewIntroKept = "Check the transcript, name the speakers, agree the actions, then continue to the sealed file. What you do here is saved with the meeting on this device, encrypted, when you tap Done."
let PROPOSED_ContinueToSealed = "Continue to the sealed file"
let PROPOSED_ReviewSealedNote = "The recording is already sealed. This screen only helps you agree the actions before everyone leaves."

// MARK: - Compose email

let PROPOSED_ComposeEmail = "Compose summary email"
let PROPOSED_ComposeTitle = "Summary email"
let PROPOSED_ComposeIntro = "Add who should get it. Your Mail app opens with the email ready; you check it and send it yourself. This app sends nothing."
let PROPOSED_RecipientField = "Email address"
let PROPOSED_AddRecipient = "Add"
/// VoiceOver: "{0}" is the address.
let PROPOSED_RemoveRecipient = "Remove %@"
let PROPOSED_RecipientInvalid = "That does not look like an email address."
let PROPOSED_OpenInMail = "Open in Mail"
let PROPOSED_MailUnavailable = "No mail app could be opened on this device."
/// "{0}" is the meeting date, DD/MM/YYYY.
let PROPOSED_EmailSubject = "Meeting summary and actions — %@"
let PROPOSED_EmailSummaryHeading = "Summary"
let PROPOSED_EmailActionsHeading = "Actions"
let PROPOSED_EmailNoSummary = "No summary was written."
let PROPOSED_EmailNoActions = "No actions were recorded."
let PROPOSED_EmailUnassigned = "Not yet assigned"
/// "{0}" is the by-when text.
let PROPOSED_EmailByWhen = " (by %@)"
let PROPOSED_EmailSealedLine = "The recording of this meeting is sealed and held by the organiser."

// MARK: - Meetings kept on this device (17/11/2026)

/// Copied by hand into Info.plist as NSFaceIDUsageDescription (a plist cannot read this constant).
let PROPOSED_FaceIDUsage = "Dictum Recorder uses Face ID to open your saved meetings."
/// The reason shown in the Face ID / passcode prompt.
let PROPOSED_FaceIDReason = "Open your saved meetings"
let PROPOSED_Meetings = "Meetings"
let PROPOSED_NoMeetings = "No meetings yet. Each meeting you record is kept here, encrypted, until you delete it."
let PROPOSED_StateSaved = "Saved on this device"
let PROPOSED_StateCodeNeeded = "Sealed — code needed"
/// Row title before the meeting is opened (the title is encrypted): "{0}" is the date and time.
let PROPOSED_MeetingOn = "Meeting on %@"
/// "{0}" is the duration, for example 12:04.
let PROPOSED_DurationAbout = "about %@"
/// VoiceOver for a meeting row: "{0}" title, "{1}" date, "{2}" duration, "{3}" size, "{4}" state.
let PROPOSED_MeetingRow = "%@, %@, %@, %@, %@"
let PROPOSED_OpenMeeting = "Open this meeting"
let PROPOSED_OpenFailed = "The meeting could not be opened."
let PROPOSED_AuthFailed = "Face ID or the passcode was not confirmed, so the meeting stays locked."
let PROPOSED_AuthUnavailable = "This device has no passcode set, so saved meetings cannot be opened. Set a passcode in Settings."
let PROPOSED_KeyMissing = "This meeting's key is no longer on this device. Unlock it with its six words."
let PROPOSED_UnlockWithWords = "Unlock with the six words"
let PROPOSED_UnlockTitle = "Unlock with the six words"
let PROPOSED_UnlockIntro = "Type the six words shown when this meeting was sealed. The meeting is then kept on this device, encrypted, and opens with Face ID or your passcode."
let PROPOSED_UnlockField = "Six words"
let PROPOSED_UnlockButton = "Unlock"
let PROPOSED_Unlocking = "Checking the words…"
let PROPOSED_UnlockWrong = "Those words do not unlock this meeting."
let PROPOSED_UnlockNeedsSix = "Type exactly six words."
let PROPOSED_UnlockFailed = "The meeting could not be unlocked."
let PROPOSED_KeepOffUnlock = "Turn on Keep meetings on this device in Settings to unlock and keep this meeting."
let PROPOSED_KeepFailed = "This meeting could not be kept on this device. The sealed file and its six words are unaffected."

// Meeting detail
let PROPOSED_MeetingBack = "Meetings"
let PROPOSED_DetailTranscript = "Transcript"
let PROPOSED_DetailNoTranscript = "No transcript was made for this meeting."
let PROPOSED_DetailSummaryField = "Summary text"
let PROPOSED_DetailNoSummary = "No summary yet."
let PROPOSED_DetailConfirmed = "Confirmed"
let PROPOSED_DetailNotConfirmed = "Not confirmed on this device"
let PROPOSED_DetailFacts = "Facts"
let PROPOSED_FactEngine = "Transcription engine"
let PROPOSED_FactStarted = "Started"
let PROPOSED_FactDuration = "Duration"
let PROPOSED_FactMeetingType = "Meeting type"
let PROPOSED_Saved = "Saved on this device"
let PROPOSED_SaveFailed = "Changes could not be saved on this device."
let PROPOSED_SendToComputer = "Send to computer"
let PROPOSED_Rewrapping = "Making a new six-word code…"
let PROPOSED_RewrapFailed = "A new code could not be made. The file is unchanged."
let PROPOSED_NewCodeNote = "This is a new code for this file. The code shown earlier no longer opens it."
let PROPOSED_PlayRecording = "Play recording"
let PROPOSED_PauseRecording = "Pause"
let PROPOSED_StopPlayback = "Stop"
let PROPOSED_Decrypting = "Opening the recording in memory…"
let PROPOSED_PlayFailed = "The recording could not be played."
let PROPOSED_NoAudio = "This meeting has no recording to play."
/// "{0}" is minutes.
let PROPOSED_PlayCapNote = "Only the first %d minutes play on this device in this build."
let PROPOSED_PlaybackPosition = "Playback position"
let PROPOSED_DeleteMeeting = "Delete meeting"
let PROPOSED_DeleteMeetingTitle = "Delete this meeting?"
let PROPOSED_DeleteMeetingMessage = "This deletes the sealed file, the transcript, the summary and the key from this device. It cannot be undone. Copies you have already sent are not affected."
let PROPOSED_DeleteMeetingConfirm = "Delete the meeting"
let PROPOSED_TranscribeHere = "Transcribe on this device"
let PROPOSED_Transcribing = "Transcribing on this device…"
let PROPOSED_TranscribeNeedsModel = "The on-device speech model is not on this device. Turn on the experimental transcript for a new meeting to download it, then try again."
let PROPOSED_TranscribeUnavailable = "Transcribing on this device needs iOS 26 or later with Apple's on-device speech model."
/// "{0}" is the system's error description.
let PROPOSED_TranscribeFailed = "The transcript could not be made: %@"
let PROPOSED_TranscribeNone = "No words were recognised in the recording."
/// The email footer for a kept meeting.
let PROPOSED_EmailKeptLine = "The recording of this meeting is kept by the organiser, encrypted."
let PROPOSED_TranscribeEarlierNotice = "This meeting was recorded under the earlier notice, which said transcripts are made on the computer that imports the file, so it is not transcribed on this device."

// Test mode (owner testing only, DEBUG builds; 17/11/2026).
let PROPOSED_SectionTestMode = "Testing"
let PROPOSED_TestModeToggle = "Use one code for every meeting"
let PROPOSED_TestModeNote = "For your own testing only. Every meeting sealed while this is on opens with the same six words, shown below. Turn it off before recording anything that is not a test."
let PROPOSED_TestModeNoCode = "The code could not be saved. Meetings will get a fresh code."

// "Your voice" set-up (Settings) and its facts line (18/11/2026).
let PROPOSED_HostVoiceTitle = "Your voice"
let PROPOSED_HostVoiceRowNote = "Record a short sample once, so your own lines in each meeting's transcript show your name."
let PROPOSED_HostVoiceIntro = "Read the passage below aloud in your normal voice for about 30 seconds. After that, in each meeting, the speaker whose voice matches yours is shown with the name you type here."
let PROPOSED_HostVoicePrivacy = "Dictum Recorder keeps a voice-print, a list of numbers made from your voice, not the recording. The recording is discarded as soon as the voice-print is made. The voice-print stays on this phone, is never put in a meeting file, and you can remove it here at any time."
let PROPOSED_HostVoiceNameField = "Name to show for you"
let PROPOSED_HostVoicePassageHeading = "Read this aloud"
let PROPOSED_HostVoicePassage = "On most mornings I make a cup of tea before I open the window and check the weather. If the sky is clear, I walk to the shops to buy bread, milk and a few pieces of fruit. Along the way I pass a small park where children play and dogs chase each other across the grass. Later I sort the post, water the plants on the back step, and write a short list of the jobs I would like to finish before dinner."
let PROPOSED_HostVoiceStart = "Start reading"
let PROPOSED_HostVoiceStop = "Stop and save"
/// "{0}" is the shortest recording in seconds, "{1}" the longest.
let PROPOSED_HostVoiceRecordingHint = "Keep reading. You can stop after %d seconds. Recording stops by itself at %d seconds."
/// "{0}" is whole seconds recorded so far.
let PROPOSED_HostVoiceSeconds = "%d seconds"
let PROPOSED_HostVoiceWorking = "Making your voice-print…"
let PROPOSED_HostVoiceNotEnough = "Not enough speech was heard to make a voice-print. Find a quiet place and try again."
let PROPOSED_HostVoiceUnavailable = "The voice model is not in this build, so a voice-print cannot be made."
let PROPOSED_HostVoiceInterrupted = "Recording was interrupted, so nothing was kept. Try again."
let PROPOSED_HostVoiceSaveFailed = "The voice-print could not be saved on this phone. Try again."
/// "{0}" is the name the user typed.
let PROPOSED_HostVoiceSavedAs = "Your voice is set up. The matching speaker in each meeting is shown as \u{201C}%@\u{201D}."
let PROPOSED_HostVoiceAgain = "Do it again"
let PROPOSED_HostVoiceRemove = "Remove my voice"
let PROPOSED_HostVoiceRemoveTitle = "Remove your voice-print?"
let PROPOSED_HostVoiceRemoveMessage = "This deletes the voice-print and the name from this phone. Names already given in kept meetings stay as they are."
let PROPOSED_HostVoiceRemoveFailed = "The voice-print could not be removed. Try again."
let PROPOSED_FactHostVoice = "Your voice"
let PROPOSED_FactHostVoiceNone = "Not set up"
let PROPOSED_FactHostVoiceNoGroups = "Set up; no speaker groups to compare"
/// "{0}" is the speaker number, "{1}" the cosine distance.
let PROPOSED_FactHostVoiceMatched = "Set up; matched speaker %d at distance %.3f"
/// "{0}" is the nearest group's cosine distance.
let PROPOSED_FactHostVoiceNoMatch = "Set up; no match (nearest distance %.3f)"
