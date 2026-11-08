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

// MARK: - Screen 2: Recording

let PROPOSED_RecordingIndicator = "Recording"
let PROPOSED_ElapsedLabel = "Elapsed time"
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
