# Demo recordings

Short captioned walkthrough videos of Dictum, recorded unattended on a clean Windows (Windows Sandbox) with no
narration, for people who have never used it. Every caption names one thing to do. Written 21/10/2026 on main at
6dfede8 (Dictum 1.1.1 plus the Vulkan launch fix); the final recordings are to be made on the 1.2 build.

All caption copy below is **PROPOSED**, for the product owner to approve.

## What it makes

For each demo, in `dist\demos\` (git-ignored):

| File | What |
|---|---|
| `<name>.mp4` | H.264, 30 fps, no audio. The sandbox screen (1600 x 900) with a caption band below it (1600 x 1116 in all). |
| `<name>.gif` | For the web: 800 px wide, 8 frames a second, made smaller until it is under 15 MB. |
| `<name>-poster.png` | The frame of the step marked `"poster": true` (else the last second). |
| `<name>\frames\NN-<step>.png` | One still per step, just before its click, with caption and highlight: for the website guide. |
| `<name>\frames\raw\NN-<step>.png` | The same moments without caption or highlight. |
| `<name>\steps.json` | The step log the video was made from (below). |
| `runs\<date-time>-<name>\` | Each recording run: `raw.mp4`, `steps.json`, `job.json`, `progress.txt`, ffmpeg's log, Dictum's own log, and a UI Automation tree for any lookup that failed. |

## Running it

Needs Windows Sandbox (the Windows feature), ffmpeg (`winget install Gyan.FFmpeg`; 9.0.2 was used), Inno Setup for
the Setup exe, and no other sandbox open. The host PC must have the Microsoft Visual C++ Redistributable (x64)
installed: the sandbox borrows it (below).

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
  stage Setup exe, VC++ DLLs  ── read-only ──▶   reports the desktop size (screen.txt)
  write job.json, demo.wsb                     ◀─ sizes the window until the desktop is 1600 x 900 (window.txt)
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
  rectangle), from a second before the click until 1.4 s after; a "point" step keeps it until its step ends.
- **The sandbox's size.** The sandbox desktop takes its size from its window when the sandbox connects; resizing the
  window afterwards (with `SetWindowPos`, and even with the end of a drag, `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE`, sent)
  left it at the window's earlier size (2736 x 1111 or 2752 x 1120) in five of twelve runs on 21/10/2026. So the host
  holds the window at 1600 x 933 from the moment it appears (every 150 ms), and when the driver still reports another
  desktop size 20 s later, it closes the sandbox and starts it again, up to three times; after that it warns and
  records at the size it got. `window.txt` in the run folder says which attempt held.
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
on screen), `"poster": true`, and its own `lead` and `after`. `environment.extraModels` names files in the host's model
folder (`-ModelsDir`, default `%LOCALAPPDATA%\Dictum\models`), mapped read-only and copied in by `modelsFromHost`: no
model is ever downloaded.

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
- **The "microphone not heard" demo cannot be recorded today.** Today the pill stays on Recording when the microphone
  hears nothing; that is the behaviour being fixed, so it is not recorded. Once the silence warning lands, a sandbox
  demo can show it if the warning also appears with no device at all; whether it does is for the 1.2 build to show.
- **A dictation, as stills.** `tests\Dictum.Tests\PillStillsTests` renders the pill in each state with Dictum's own
  code on Avalonia's headless platform with Skia (`CaptureRenderedFrame`), at three times its size, and checks each
  frame (the pill's navy is drawn; no two states are the same). With `DICTUM_STILLS_DIR` set it saves
  `pill-<state>.png`; `scripts\demo-stills.ps1` lays them on a plain background with the same captions
  (`scripts\demos\dictation-stills.json`, kind `stills`). It is an illustration of the indicator, not a recording:
  no text is shown being typed. The headless frame has an opaque black background (no window transparency there),
  which the script keys out; the pill's shadow goes with it. The tray panel and Settings could be rendered the same
  way, but they need a running shell (`AppShell`), and the sandbox already records them for real.
- **No network.** SmartScreen cannot reach Microsoft, so the install demo shows "SmartScreen can't be reached right
  now" (Run / Don't run) where a real download shows "Windows protected your PC" (More info, then Run anyway). The data
  file has optional steps for both; only the offline one has been seen in the sandbox.
- **The Visual C++ runtime is borrowed.** Dictum's speech engine needs the Microsoft Visual C++ Redistributable, which
  a clean Windows lacks (docs\DECISIONS.md, "Visual C++ runtime"). The sandbox gets this PC's copy (the four DLLs
  into its System32 and the registry key Setup checks), so it is a PC with the redistributable installed. Nothing is
  redistributed: the files go only into the disposable sandbox. `-NoVcRuntime` records a PC without it (Setup then
  offers Microsoft's download first).
- **The second drive is virtual**: a 2 GB virtual disk inside the sandbox, shown as "Data (D:)".
- **The sandbox runs this PC's Windows build**, an Insider Preview here, so the taskbar, dialogs and watermark are
  that build's.
- **Calibration today**: the 1.2 build is to rename "Calibrate my voice" to "Test my setup"; the draft uses today's
  name. With no microphone it shows "That did not work: No microphone found..." after Record.

## What each demo shows today (drafts of 21/10/2026)

| Demo | File | Shows |
|---|---|---|
| 1. Install | `install.json` | Downloads folder, double-click Setup, SmartScreen (offline variant), Next, Install, Launch Dictum ticked, Finish, Dictum's first window |
| 2. First run | `first-run.json` | Start, type Dictum, Enter, the hotkey check (Right Ctrl), Use this hotkey, the tray icon, the panel showing Ready |
| 3. Change the speech model | `change-model.json` | Tray icon, Settings, Models, Use next to tiny.en (on the PC already), In use |
| 4. Test my setup | `test-setup.json` | Tray icon, Calibrate my voice, the first sentence, Record, and the no-microphone message |
| 5. Models on another drive | `model-folder.json` | Settings, Models, scroll to Model folder, Browse, type D:\Dictum models, Select Folder, the new folder shown |
| 6. Microphone not heard | none yet | Not recordable until the silence warning lands (above) |
| Dictation (stills) | `dictation-stills.json` | The pill: Recording, Transcribing, Pasted 12 words |

## PROPOSED caption copy

1. **Install Dictum**
   1. Open your Downloads folder. Double-click Dictum-Setup.
   2. If Windows says it protected your PC, click More info. *(online only; not seen in the sandbox)*
   3. Then click Run anyway. *(online only)*
   4. If Windows says SmartScreen can't be reached, click Run.
   5. Setup opens. Click Next.
   6. Click Install.
   7. When it has finished, leave Launch Dictum ticked.
   8. Click Finish.
   9. Dictum is installed and starts. First it asks about your hotkey.
2. **Start Dictum for the first time**
   1. Click the Start button.
   2. Type Dictum.
   3. Press Enter to open it.
   4. Dictum asks which key you hold while you speak. Right Ctrl is already chosen.
   5. Click Use this hotkey.
   6. Dictum now waits by the clock. Click the little up-arrow there.
   7. Click the Dictum icon.
   8. This is Dictum's panel. Ready means you can dictate.
   9. Press Esc to close the panel.
3. **Change the speech model**
   1. Click the little up-arrow near the clock, at the bottom right of the screen.
   2. Click the Dictum icon.
   3. Click Settings.
   4. Click Models, on the left.
   5. Find the model you want. Click Use next to it.
   6. That's it. The model you chose now says In use.
4. **Test your setup** (today: Calibrate my voice)
   1. Click the little up-arrow near the clock, at the bottom right of the screen.
   2. Click the Dictum icon.
   3. Click Calibrate my voice.
   4. Dictum shows a sentence to read aloud.
   5. Click Record, then read the sentence in your normal voice.
   6. This test PC has no microphone, so Dictum says so here. *(draft only: the 1.2 recording needs a different ending)*
5. **Keep the models on another drive**
   1. Click the little up-arrow near the clock, at the bottom right of the screen.
   2. Click the Dictum icon.
   3. Click Settings.
   4. Click Models, on the left.
   5. Scroll down to Model folder. Click Browse.
   6. Type the folder you want, for example D:\Dictum models.
   7. Click Select Folder.
   8. Done. Dictum now keeps new models on drive D.
- **Dictate: hold, speak, let go** (stills)
  1. Hold Right Ctrl and speak. The bar at the bottom of the screen says Recording.
  2. Let go of the key when you have finished. Dictum turns your words into text.
  3. Your words appear where your cursor was. The bar says how many.

"The little up-arrow" appears only when Windows has put Dictum's icon in the hidden-icons area, as it did in every
sandbox run; when the icon is already on the taskbar that step is skipped and the numbering closes up.
