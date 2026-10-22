# Demo recordings

Short captioned walkthrough videos of Dictum, recorded unattended on a clean Windows (Windows Sandbox) with no
narration, for people who have never used it. Every caption names one thing to do. Pipeline written 21/10/2026 on
main at 6dfede8; the demos were re-recorded on 22/10/2026 against the 1.2 build on main at d47e7f9 (both polish rounds), below.

All caption copy below is **PROPOSED**, for the product owner to approve.

## What it makes

For each demo, in `dist\demos\` (git-ignored):

| File | What |
|---|---|
| `<name>.mp4` | H.264, 30 fps, no audio. The sandbox screen (1600 x 900) with a caption band below it (1600 x 1116 in all). |
| `<name>.gif` | For the web: 800 px wide, 8 frames a second, made smaller until it is under 15 MB. |
| `<name>-poster.png` | The frame of the step marked `"poster": true` (else the last second). |
| `<name>\frames\NN-<step>.png` | One still per step, with caption and highlight: for the website guide. Taken 0.35 s before the click (halfway through a step with no click); for a step that types, 0.3 s after the typing ended, so it shows the typed text. |
| `<name>\frames\raw\NN-<step>.png` | The same moments without caption or highlight. |
| `<name>\steps.json` | The step log the video was made from (below). |
| `runs\<date-time>-<name>\` | Each recording run: `raw.mp4`, `steps.json`, `job.json`, `progress.txt`, ffmpeg's log, Dictum's own log, and a UI Automation tree for any lookup that failed. |

## Running it

Needs Windows Sandbox (the Windows feature), ffmpeg (`winget install Gyan.FFmpeg`; 9.0.2 was used), Inno Setup for
the Setup exe, and no other sandbox open. The host PC must have the Microsoft Visual C++ Redistributable (x64)
installed: the sandbox borrows it (below). The host also needs:

- **100 % display scaling.** The window sizing assumes the sandbox's title bar is 33 px, which holds at 100 % only.
  `demo-record.ps1` stops with "needs 100 % display scaling on the host" when either of two reads is not 96: the
  primary monitor's effective DPI (shcore `GetDpiForMonitor`, asked from a thread switched to per-monitor-v2 DPI
  awareness for that one call, because `powershell.exe` is DPI-unaware and Windows then reports 96 whatever the
  scaling), or the registry value `HKCU:\Control Panel\Desktop\WindowMetrics\AppliedDPI`. `-SkipDpiCheck` turns the
  check off (the sandbox desktop may then never reach 1600 x 900). Not verified: any host at a scaling other than
  100 %; the check has only been run on a PC where both reads gave 96, so that it stops a scaled host is untested here.
- **A screen that fits the window.** The sandbox desktop is 1600 x 900 inside a 1600 x 933 window (the title bar adds
  33 px) plus borders, so the host screen must be larger than that: a 1366 x 768 screen cannot. If the sandbox still
  comes up at another size after `-SizeAttempts` tries, the script stops (below); `-AllowOtherSize` records at that size
  anyway.

```powershell
# 1. The Setup exe of the build to show (publishes dist\Dictum first)
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1

# 2. Record and caption (all recorded demos, or some: -Demo install,change-model)
powershell -ExecutionPolicy Bypass -File scripts\demo-record.ps1

# 3. The stills walkthrough of a dictation (no sandbox)
powershell -ExecutionPolicy Bypass -File scripts\demo-stills.ps1

# Re-caption a run after editing captions in scripts\demos\<name>.json (no new recording)
powershell -ExecutionPolicy Bypass -File scripts\demo-post.ps1 -Run dist\demos\runs\<date-time>-<name>
```

Each demo takes one sandbox session, about three to four minutes. The sandbox window opens on the desktop and closes
itself. The recording is made inside the sandbox (below), so the host PC stays usable while it records.

Nothing on the host changes except `dist\demos` and a staging folder in `%TEMP%` that is deleted after each run. The
sandbox has networking, the microphone, the camera, the clipboard and printers off, and every mapped folder is
read-only except the run's own folder.

## How it works

```
demo-record.ps1 (host)                         demo-inside.ps1 (inside Windows Sandbox, hidden at logon)
  stage Setup exe, VC++ DLLs,  ── read-only ──▶  reports the desktop size (screen.txt)
  extra model files (copies)                   ◀─ sizes the window until the desktop is 1600 x 900 (window.txt)
  write job.json (run folder), demo.wsb (staging folder)
  start the sandbox                              installs the VC++ runtime, then the "prepare" actions (not recorded)
  size its window                                starts ffmpeg gdigrab on its own desktop
                                                 runs each step: find the target, show the caption, glide, click
  wait for done.txt  ◀──────── run folder ────   writes raw.mp4, steps.json, done.txt; shuts the sandbox down
demo-post.ps1 (host): captions + highlights from steps.json → mp4, gif, poster, frames
```

- **Driving.** `demo-inside.ps1` finds every target with Windows UI Automation (`scripts\demo-ui.cs`, compiled in the
  sandbox by Windows PowerShell 5.1), by the names Dictum's windows set (`AutomationProperties.Name`, or a button's
  own text). The real cursor glides to the target over 0.8 s, rests 0.4 s, and clicks with `SendInput`, so the video
  shows what a person would do. A step's caption appears only once its target is on screen, then stays 2 s before the
  pointer moves; the result stays 1.6 s before the next step. These are the `pace` defaults; a demo or a step can
  change them.
- **Fallbacks are recorded, never silent.** If a target is out of view and cannot be scrolled into it, the step uses
  the element's UI Automation pattern (Invoke, SelectionItem or Toggle) instead of a click; if a lookup fails and the
  step gives `"at"` coordinates, the click goes there. Either way `steps.json` lists it under `fallbacks`, and a
  pattern click gets no highlight in the video. The drafts of 21/10/2026 used none.
- **Timing.** Each step's start and click are wall-clock times on the sandbox's clock. ffmpeg's gdigrab logs the
  wall-clock time of its first frame (`start:` in its log, kept as `videoStartEpoch`), which puts both on one clock;
  ffmpeg takes about 5 s to start inside the sandbox (observed), and that delay does not matter.
- **Captions** sit in a dark band added **below** the screen, not over it, so a caption never hides the taskbar, the
  tray icon or the dialog being clicked. White on near-black, 47 px at 900 (5.2 % of the screen height), centred, at
  most two lines (the post-processor warns when a caption needs a third), with "Step n of m" above it and the demo's
  title in yellow before the first step. Captions come from the data file by step id, so rewording one needs only
  `demo-post.ps1`, not a new recording.
- **Highlights**: a yellow rounded rectangle with a dark edge around the clicked element (its UI Automation
  rectangle), from a second before the click (never before the step's caption) until 0.3 s after it, since a click often
  closes the window or scrolls the page; a "point" step keeps it until the next step's caption starts.
- **The sandbox's size.** The sandbox desktop takes its size from its window when the sandbox connects; resizing the
  window afterwards (with `SetWindowPos`, and even with the end of a drag, `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE`, sent)
  left it at the window's earlier size (2736 x 1111 or 2752 x 1120) in five of twelve runs on 21/10/2026. So the host
  holds the window at 1600 x 933 from the moment it appears (every 150 ms), and when the driver still reports another
  desktop size 20 s later, it closes the sandbox and starts it again, up to `-SizeAttempts` (default 6) in all; after
  that it **stops with an error** rather than record at the wrong size, unless `-AllowOtherSize` is given (then it warns
  and records at the size it got). Each attempt first deletes the previous attempt's `screen.txt`, `progress.txt`,
  `done.txt`, `steps.json` and `raw.mp4`, so nothing stale is read as a finished run; and if the driver writes
  `done.txt` before any recording (it failed early), the script stops with its message. `window.txt` in the run folder
  says which attempt held.
- **What is shared with the sandbox.** Read-only: the scripts folder, a staged copy of the Setup exe, the Visual C++
  files, ffmpeg, and, when a demo names extra models, a staging folder holding copies of only those model files (the
  host's own model folder is never mapped). Writable: the run folder, which holds no host path (`demo.wsb`, which lists
  them, is written to the staging folder in `%TEMP%`, deleted after the run). `summary.json` also holds paths relative
  to the output folder only.
- **A clean desktop.** Each demo's first prepare action is `plainDesktop`: a plain blue background and the desktop
  icons hidden. The sandbox's "Windows 11 Enterprise Insider Preview, Evaluation copy" watermark (bottom right) stays:
  Windows draws it.

### Why the recording is made inside the sandbox

ffmpeg (the static build, mapped read-only) records the sandbox's own desktop with `gdigrab -i desktop`, inside the
sandbox. Recording the sandbox window from the host was built first, as briefed, and dropped on 21/10/2026 after two
runs:

- `gdigrab -i "title=Windows Sandbox"` finds the window and records its client area (1600 x 933, the sandbox's 33 px
  title bar included), but every frame was plain white: the sandbox window draws through DirectComposition, which a
  window's GDI surface does not hold.
- Recording the screen area under that client area instead (`-offset_x`, `-offset_y`, `-video_size`, `-i desktop`)
  records whatever is on top there. In the second run a host window covered the sandbox, and the video showed that
  window's content, private text included. A recording made on a PC someone is using can leak what is on it.

Inside, nothing on the host can appear in a video, and the host PC stays usable while a demo records.

## A demo's data file

`scripts\demos\<name>.json`:

```json
{
  "name": "change-model",
  "title": "Change the speech model",
  "summary": "What it shows, for this page.",
  "environment": { "extraModels": ["ggml-tiny.en.bin"] },
  "pace": { "lead": 2.0, "glide": 0.8, "hover": 0.4, "after": 1.6, "typing": 70 },
  "prepare": [ { "do": "installSilently" }, { "do": "launch" } ],
  "steps": [
    { "id": "settings", "caption": "Click Settings.",
      "do": [ { "do": "click", "target": { "window": "^Dictum$", "type": "Button", "name": "Settings" } } ] }
  ]
}
```

`prepare` runs before recording starts; `steps` are recorded, one caption each. A step can carry `"optional": true`
(skipped, and not shown, when its first target does not appear), `"skipIf": <target>` (skipped when that is already
on screen), `"poster": true`, `"scrollFirst": true` (its first target is scrolled into view as the caption
appears: the caption changes first, then the page scrolls, for text further down a page), and its own `lead` and `after`. `environment.extraModels` names files in the host's model
folder (`-ModelsDir`, default `%LOCALAPPDATA%\Dictum\models`). Only those files are copied, to a staging folder that is
mapped read-only, and then copied in by `modelsFromHost`: the folder itself is never mapped and no model is ever
downloaded.

A **target** is `window` (a regular expression on the top-level window's title), `windowClass`, `type` (Button, Edit,
Text, ListItem, CheckBox, ...), `name` with `match` (`exact`, the default and case-insensitive, `prefix`,
`contains` or `regex`), `automationId`, `sameRowAs` (several buttons share a name, such as "Use": the one level with
the first element whose name starts with this text), `index`, `timeout` in seconds (default 30) and `at`
(`{ "x": 0.5, "y": 0.9 }`, fractions of the screen, used only when the lookup fails, and recorded as a fallback).

| Action (`"do"`) | Does |
|---|---|
| `click`, `doubleClick`, `point` | Glide to the target and click, double-click, or only rest beside it (the highlight shows it) |
| `type` | Type `text` visibly; with `into`, click that target first and select its text (`"replace": false` keeps it) |
| `key` | Press `keys`: Enter, Escape, Tab, Win, Ctrl+A, RightCtrl, ... (`scripts\demo-ui.cs` lists them) |
| `wait` | Wait `seconds` |
| `waitFor` | Wait for `target` (or, with `"gone": true`, for it to go); `"optional": true` carries on if it never comes |
| `installSilently` | Run the Setup exe with `/VERYSILENT` |
| `launch` | Start `path` (default the installed `Dictum.exe`; `{app}` is a published folder given with `-App`) |
| `copy`, `copyDownloaded` | Copy `from` to `to`; `copyDownloaded` also marks the copy as downloaded from the internet (zone 3) |
| `modelsFromHost` | Copy `environment.extraModels` into the sandbox's model folder |
| `secondDrive` | Make drive `letter` (a 2 GB virtual disk labelled `label`, or `subst` if that fails) with `folder` on it |
| `openFolder` | Open `path` in File Explorer |
| `plainDesktop` | Plain background, desktop icons hidden |

Paths expand environment variables (`%USERPROFILE%`) and `{setup}`, `{app}`, `{installed}`.

### Adding a demo

1. Write `scripts\demos\<name>.json`, starting from the closest existing one.
2. Find each target's name: run the demo; a failed lookup saves the whole UI Automation tree of the windows it searched
   as `uia-<step>.txt` in the run folder, one line per element with its type, name and rectangle.
3. `scripts\demo-record.ps1 -Demo <name>`, then look at `dist\demos\<name>\frames\`.
4. Reword captions in the data file and re-run only `demo-post.ps1 -Run <run folder>`.

For 1.2, a new or renamed control needs only its data file changed: the scripts know nothing about Dictum's windows.

## Limitations

- **No microphone, no sound.** Windows Sandbox here has no audio input (the driver logs `audio capture devices: 0`), so
  no dictation can be recorded and the videos are silent. Nothing that depends on speech can be shown live.
- **The welcome's microphone step shows the no-microphone message.** With no device at all, the 1.2 welcome's
  Microphone step says "No microphone found. Plug one in, or on a remote or virtual desktop turn on microphone
  redirection, then try again." The first-run demo shows that, with a caption saying the test PC has none. "Heard you."
  and "Nothing heard." (a microphone that hears silence) cannot be shown here.
- **Try it cannot be tried.** The welcome's last step shows its instruction and the practice box; with no microphone,
  nothing is dictated, so the demo moves on with Finish.
- **A dictation, as stills.** `tests\Dictum.Tests\PillStillsTests` renders the pill in each state with Dictum's own
  code on Avalonia's headless platform with Skia (`CaptureRenderedFrame`), at three times its size, and checks each
  frame (the pill's navy is drawn; no two states are the same). With `DICTUM_STILLS_DIR` set it saves
  `pill-<state>.png`; `scripts\demo-stills.ps1` lays them on a plain background with the same captions
  (`scripts\demos\dictation-stills.json`, kind `stills`). It is an illustration of the indicator, not a recording:
  no text is shown being typed. The headless frame has an opaque black background (no window transparency there),
  which the script keys out; the pill's shadow goes with it. The 1.2 stills were rendered by the 1.2 code (the script
  run from a checkout of that build, `-Out` pointing here). 1.2.1 changed the hold pill to "Recording · let go to
  finish" (owner decision 3, 22/10/2026), so `dictation-stills` was re-rendered on branch `fix/1.2.1-copy`
  (`scripts\demo-stills.ps1 -Out <folder>`, headless, no sandbox) and `site/img/demos/dictation-stills.gif` replaced;
  the poster (the last slide, "Pasted 12 words") came out byte-identical and is unchanged. Whether a frame of any other demo shows the hold pill was not checked.
- **Due for a retake on the 1.2.1 build (not re-recorded yet).** `change-model`, `help` and `test-setup`, with their
  GIFs and posters in `site/img/demos/`, are 1.2.0 footage. Per the 1.2.1 lens (22/10/2026), they show the tray panel's
  Light | Full switch and its Mode line, which the Simple view now hides (owner decision 5), and the state word "Ready"
  beside "No microphone found", where 1.2.1 says "No microphone" (owner decision 4). Record them again from a 1.2.1
  build; until then they show the 1.2.0 panel.
- **No network.** SmartScreen cannot reach Microsoft, so the install demo shows "SmartScreen can't be reached right
  now" (Run / Don't run) where a real download shows "Windows protected your PC" (More info, then Run anyway). The data
  file has optional steps for both; only the offline one has been seen in the sandbox.
- **The SmartScreen frame is Windows' own text for an unsigned file.** It shows the placeholder string
  "File Type: @@FileType@@" and "Publisher: Unknown Publisher" because the local build is unsigned. The published Setup
  may differ, so re-check this frame on the build that is shown.
- **The Visual C++ runtime is borrowed.** Dictum's speech engine needs the Microsoft Visual C++ Redistributable, which
  a clean Windows lacks (docs\DECISIONS.md, "Visual C++ runtime"). The sandbox gets this PC's copy (the four DLLs
  into its System32 and the registry key Setup checks), so it is a PC with the redistributable installed. Nothing is
  redistributed: the files go only into the disposable sandbox. `-NoVcRuntime` records a PC without it (Setup then
  offers Microsoft's download first).
- **The second drive is virtual**: a 2 GB virtual disk inside the sandbox, shown as "Data (D:)".
- **The sandbox runs this PC's Windows build**, an Insider Preview here, so the taskbar, dialogs and watermark are
  that build's.
- **Calibration keeps its name.** The 1.2 build still calls it "Calibrate my voice" (the tray panel, Settings > Models,
  the welcome's Try it step and Help all use that name), so the demo does too; its file stays `test-setup.json`. With
  no microphone, Record is off and "No microphone found. Plug one in, ..." stands under it (the same sentence as the welcome window
  and the tray panel).
- **Not yet shown: the Help paragraph "In this help, … Simple view …".** The help demo jumps past it, so that sentence
  is never on screen. Noted for a later recording (lens on 7900e49).
- **The help demo does not press Send feedback**: it opens the user's own mail program, and the sandbox has none. The
  demo shows the button and says what it does.
- **The Setup's version is 1.1.2.** The 1.2 build (main at d47e7f9) still carries version 1.1.2, so the Setup file,
  the SmartScreen dialog and Setup's title say 1.1.2 in these recordings.

## What each demo shows (1.2 build, 21/10/2026)

Recorded on 22/10/2026 from main at d47e7f9 (the 1.2 build of PR #23 and both polish rounds, PRs #38 and #39). install was recorded
at 4abba54 (PR #38): its screens (Setup and the welcome's first step) did not change in #39. CI on a branch
publishes only the zip (`.github/workflows/ci.yml` builds the Setup on tags), so the Setup exe was built locally from
that commit with `scripts\build-installer.ps1` (Inno Setup 7.1.0): `Dictum-Setup-1.1.2.exe`, SHA-256
`3AC40A529606027DA11524AD447B467BC8E57CAFEBFB37938B87AF3594121AF8`, `Dictum.exe` version `1.1.2+d47e7f9` (install: the
Setup built at 4abba54, SHA-256 `D12A9D96DCD5906DD70A7AADF023A87D91DAA0C62F794691883A4A2D9200DB20`). The web copies are in `site\img\demos\`:
`<name>.gif` and `<name>-poster.png` are committed; `<name>.mp4` is not, because the private-material guard
(`.githooks/private-guard.sh`, and `.gitignore`) refuses every `.mp4` in the repository, with no exception. Where the
videos are hosted is the product owner's decision.

| Demo | File | Shows |
|---|---|---|
| 1. Install | `install.json` | Downloads folder, double-click Setup, SmartScreen (offline variant), Next, Install, Launch Dictum ticked, Finish, the "Welcome to Dictum" window |
| 2. First run | `first-run.json` | Start, type Dictum, Enter, the six-step welcome (Welcome, Model files, Speech model, Your hotkey, Microphone with the no-microphone message, Try it while the model gets ready), Finish, the tray icon, the panel showing Ready and the no-microphone sentence |
| 3. Change the speech model | `change-model.json` | Settings > Models in the Simple view: Standard in use, Use next to More accurate (small.en, copied in from the host's model folder, read-only), In use, then Advanced showing every model by name |
| 4. Calibrate my voice | `test-setup.json` | Tray icon, Calibrate my voice, the first sentence, Record, and Record switched off, with the no-microphone message under it |
| 5. Models on another drive | `model-folder.json` | Settings, Models, Browse, type D:\Dictum models, Select Folder, the new folder shown |
| 6. Find help | `help.json` | Settings > Help: the "Questions on this page" list, two questions opened from it and their answers, Send feedback (shown, not pressed) |
| Dictation (stills) | `dictation-stills.json` | The pill: Recording, Transcribing, Pasted 12 words |

Posters (the step marked `"poster": true`): install, step 9 (the welcome window's first step); first-run, step 12 (Try it,
the box ready); change-model, step 7 (More accurate In use); test-setup, step 4 (the sentence to read); model-folder, step 8
(the new folder shown); help, step 10 (Send feedback); dictation-stills, its last slide (Pasted 12 words). None is an
error screen, except that the no-microphone sentence under the disabled Record is visible in test-setup's: on the 1.2
build it shows from the moment the calibration window opens on a PC with no microphone, so no frame of that demo is
without it.

change-model, test-setup, model-folder and help go through the welcome window with its defaults (Next, Next, Next,
Use this hotkey, Next, Finish) before recording starts. install and first-run do not: install ends on the welcome
window, and first-run records it.

## PROPOSED caption copy

1. **Install Dictum** (`install.json`)
   1. Double-click Dictum-Setup in your Downloads folder.
   2. If Windows says it protected your PC, click More info. *(online only: not seen in these recordings, so the video counts 7 steps)*
   3. Then click Run anyway. *(online only: not seen in these recordings, so the video counts 7 steps)*
   4. If Windows says SmartScreen can't be reached, click Run. *(only when that dialog appears)*
   5. Setup opens. Click Next.
   6. Click Install.
   7. When it has finished, leave Launch Dictum ticked.
   8. Click Finish.
   9. Dictum starts and welcomes you. A few short steps set it up.
2. **Start Dictum for the first time** (`first-run.json`)
   1. Click the Start button.
   2. Type Dictum.
   3. Press Enter to open it.
   4. Dictum explains how it works. Click Next.
   5. Dictum suggests where to keep its files. Click Next to use that folder.
   6. The speech model comes with Dictum and is ready. Click Next.
   7. This is the key you hold while you speak. Right Ctrl is already chosen.
   8. Click Use this hotkey.
   9. Dictum tests your microphone. This test PC has none, so it says so.
   10. Click Next. With a microphone, you would say a few words first.
   11. Dictum gets the speech model ready. This takes a moment.
   12. Last, Try it: with a microphone, you would dictate into this box.
   13. Click Finish.
   14. Dictum now waits by the clock. Click the little up-arrow there. *(skipped when the icon is already on the taskbar)*
   15. Click the Dictum icon.
   16. This is Dictum's panel. Ready means it is waiting for you.
   17. This test PC has no microphone, so the panel says so here.
   18. Press Esc to close the panel.
3. **Change the speech model** (`change-model.json`)
   1. Click the little up-arrow near the clock, at the bottom right of the screen. *(skipped when the icon is already on the taskbar)*
   2. Click the Dictum icon.
   3. Click Settings.
   4. Click Models, on the left.
   5. Standard is in use. It works well for most people.
   6. If words often come out wrong, click Use next to More accurate.
   7. That's it. More accurate now says In use.
   8. To see every model by its name and size, click Advanced.
   9. Advanced lists every model by name. small.en is in use.
4. **Calibrate my voice** (`test-setup.json`)
   1. Click the little up-arrow near the clock, at the bottom right of the screen. *(skipped when the icon is already on the taskbar)*
   2. Click the Dictum icon.
   3. Click Calibrate my voice.
   4. Dictum shows a sentence to read aloud.
   5. With a microphone, you would click Record and read the sentence aloud.
   6. This test PC has none, so Record is off and Dictum says why.
5. **Keep the models on another drive** (`model-folder.json`)
   1. Click the little up-arrow near the clock, at the bottom right of the screen. *(skipped when the icon is already on the taskbar)*
   2. Click the Dictum icon.
   3. Click Settings.
   4. Click Models, on the left.
   5. Click Browse, under Model folder.
   6. Type the folder you want, for example D:\Dictum models.
   7. Click Select Folder.
   8. Done. Dictum now keeps new models on drive D.
6. **Find help in Dictum** (`help.json`)
   1. Click the little up-arrow near the clock, at the bottom right of the screen. *(skipped when the icon is already on the taskbar)*
   2. Click the Dictum icon.
   3. Click Settings.
   4. Click Help, at the bottom of the list on the left.
   5. Help starts with a list of the questions it answers.
   6. Click a question, for example Where does my voice go?
   7. Help moves down to its answer.
   8. Click another question: Why did it type the wrong words?
   9. Its answer lists what to try.
   10. To tell us something, use Send feedback. It opens an email you can check, then send.
- **Dictate: hold, speak, let go** (stills, `dictation-stills.json`)
  1. Hold Right Ctrl and speak. The bar at the bottom of the screen says Recording.
  2. Let go of the key when you have finished. Dictum turns your words into text.
  3. Your words appear where your cursor was. The bar says how many.

"The little up-arrow" appears only when Windows has put Dictum's icon in the hidden-icons area, as it did in every
sandbox run; when the icon is already on the taskbar that step is skipped and the numbering closes up.
