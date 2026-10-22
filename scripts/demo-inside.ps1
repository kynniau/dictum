# Runs inside Windows Sandbox for scripts\demo-record.ps1: reads C:\sbx\out\job.json (the demo's data file and where
# everything is mapped), prepares the clean Windows (Visual C++ runtime, a silent install, extra models, a second
# drive), then records the demo's steps: ffmpeg captures the sandbox desktop while this script drives Dictum through
# Windows UI Automation with deliberate pauses. It writes steps.json (one entry per step: when it started, its caption, where it clicked, any fallback) and
# done.txt beside job.json, then shuts the sandbox down. Never run it anywhere else: it installs software, changes
# System32 and the registry, moves the mouse and presses keys, which is harmless only in a sandbox discarded on close.
Set-StrictMode -Version Latest
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw "demo-inside.ps1 runs only inside Windows Sandbox." }
$ErrorActionPreference = "Stop"
$out = "C:\sbx\out"
$work = "C:\demo"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Note([string]$Line) { "$(Get-Date -Format HH:mm:ss.fff) $Line" | Add-Content -LiteralPath "$out\progress.txt" -Encoding UTF8 }
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding $false)) }
function Epoch { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 }
function Expand([string]$Path) {
    if (-not $Path) { return $Path }
    $p = $Path.Replace("{setup}", [string]$script:job.setupExe).Replace("{app}", [string]$script:job.app).Replace("{installed}", $script:installedExe)
    return [Environment]::ExpandEnvironmentVariables($p)
}
function Value($Object, [string]$Name, $Default) {
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name] -and $null -ne $Object.$Name) { return $Object.$Name }
    return $Default
}

$job = Get-Content -LiteralPath "$out\job.json" -Raw | ConvertFrom-Json
$demo = $job.demo
$installedExe = Join-Path $env:LOCALAPPDATA "Programs\Dictum\Dictum.exe"
$demoPace = Value $demo "pace" $null # strict mode: a demo without "pace" has no such property
$pace = [ordered]@{
    lead = [double](Value $demoPace "lead" 2.0)    # caption on screen before the pointer moves
    glide = [double](Value $demoPace "glide" 0.8)  # the pointer's travel to its target
    hover = [double](Value $demoPace "hover" 0.4)  # resting on the target before the click
    after = [double](Value $demoPace "after" 1.6)  # the result stays on screen before the next step
    typing = [int](Value $demoPace "typing" 70)    # milliseconds per typed character
}
$log = [ordered]@{
    demo = $demo.name; title = (Value $demo "title" $null); clock = "sandbox"
    screen = $null; videoStartEpoch = $null; recordingRequestedEpoch = $null
    environment = @(); prepare = @(); steps = @(); fallbacks = @(); result = "not started"
}
$ffmpeg = $null
$typedEpoch = $null # when the last "type" action of the current step finished typing (every type action, with or without "into")

# ---- the target of a pointer action, found by UI Automation ----

function Find-Target($Spec, [int]$TimeoutMs) {
    $timeout = if ($TimeoutMs -gt 0) { $TimeoutMs } else { [int]([double](Value $Spec "timeout" 30) * 1000) }
    return [DemoUi]::Find((Value $Spec "window" $null), (Value $Spec "windowClass" $null), (Value $Spec "name" $null), (Value $Spec "match" "exact"),
        (Value $Spec "type" $null), (Value $Spec "automationId" $null), (Value $Spec "sameRowAs" $null), [int](Value $Spec "index" 0), $timeout)
}

function Describe($Spec) {
    $parts = @()
    foreach ($k in "window", "windowClass", "type", "name", "match", "sameRowAs", "index") { if ($null -ne (Value $Spec $k $null)) { $parts += "$k=$($Spec.$k)" } }
    return $parts -join " "
}

function Save-Tree($Spec, [string]$Label) {
    $file = "$out\uia-$Label.txt"
    $lines = @([DemoUi]::Dump((Value $Spec "window" $null), 4000))
    $windows = @([DemoUi]::Windows($null, $null) | ForEach-Object { try { "window '$($_.Current.Name)' class=$($_.Current.ClassName)" } catch { } })
    Write-Utf8 $file ((@("Top-level windows:") + $windows + @("", "Elements under the windows searched:") + $lines) -join "`r`n")
    return $file
}

# Moves the cursor to the element and clicks it (or double-clicks, or only points at it). Returns the click record.
function Invoke-Pointer($Spec, [string]$Kind, [string]$Label) {
    $element = Find-Target $Spec 0
    $fallback = $null
    if (-not $element) {
        $at = Value $Spec "at" $null
        if (-not $at) {
            $tree = Save-Tree $Spec $Label
            throw "Not found: $(Describe $Spec) (UI Automation tree saved to $(Split-Path -Leaf $tree))"
        }
        $x = [int]([double]$at.x * [DemoUi]::ScreenWidth); $y = [int]([double]$at.y * [DemoUi]::ScreenHeight)
        $fallback = "coordinates ($x, $y): no UI Automation match for $(Describe $Spec)"
    } else {
        if (-not [DemoUi]::EnsureVisible($element, [int]($pace.glide * 1000))) { $fallback = "invoke: $(Describe $Spec) could not be scrolled into view" }
        $p = [DemoUi]::ClickPoint($element)
        $x = $p.X; $y = $p.Y
    }
    # The element's rectangle for the highlight, read now: after the click its window may be gone, and a gone
    # SmartScreen dialog reported a rectangle elsewhere on the screen (21/10/2026). Kept only if it holds the click point.
    $box = $null
    if ($element) {
        $rect = $element.Current.BoundingRectangle
        if (-not $rect.IsEmpty -and $x -ge $rect.Left - 1 -and $x -le $rect.Right + 1 -and $y -ge $rect.Top - 1 -and $y -le $rect.Bottom + 1) {
            $box = [ordered]@{ x = [int]$rect.Left; y = [int]$rect.Top; w = [int]$rect.Width; h = [int]$rect.Height }
        }
    }
    if ($fallback -and $fallback.StartsWith("invoke")) {
        if (-not [DemoUi]::Invoke($element)) { throw "Out of view and offers no Invoke, SelectionItem or Toggle pattern: $(Describe $Spec)" }
        $script:log.fallbacks += [ordered]@{ step = $Label; fallback = $fallback }
        Note "fallback in ${Label}: $fallback"
        return [ordered]@{ epoch = (Epoch); x = $null; y = $null; kind = "invoke"; fallback = $fallback }
    }
    $cx = $x; $cy = $y
    if ($Kind -eq "point" -and $element) {
        # Pointing rests the cursor just below and right of the element, so its words stay readable; the highlight
        # (demo-post.ps1) circles the element itself.
        $rect = $element.Current.BoundingRectangle
        $cx = [int]($rect.Right + 6); $cy = [int]($rect.Bottom + 6)
    }
    [DemoUi]::Glide($cx, $cy, [int]($pace.glide * 1000))
    Start-Sleep -Milliseconds ([int]($pace.hover * 1000))
    $epoch = Epoch
    switch ($Kind) {
        "click" { [DemoUi]::Click() }
        "invokeClick" {
            # The pointer glides and rests as for a click, then the element is invoked through UI Automation: a mouse click on a second
            # Dictum window (the consent dialogs) did not reach it in the sandbox (21 to 23/10/2026: the dialog went behind Settings and
            # nothing was chosen), while a key press did. Always listed under fallbacks.
            if (-not $element -or -not [DemoUi]::Invoke($element)) { throw "invokeClick: no Invoke, SelectionItem or Toggle pattern: $(Describe $Spec)" }
            $script:log.fallbacks += [ordered]@{ step = $Label; fallback = "invoke after the pointer rested on it: a mouse click does not reach this dialog in the sandbox: $(Describe $Spec)" }
        }
        "doubleClick" { [DemoUi]::DoubleClick() }
        "point" { }
    }
    if ($fallback) {
        $script:log.fallbacks += [ordered]@{ step = $Label; fallback = $fallback }
        Note "fallback in ${Label}: $fallback"
    }
    return [ordered]@{ epoch = $epoch; x = $x; y = $y; nx = [math]::Round($x / [DemoUi]::ScreenWidth, 4); ny = [math]::Round($y / [DemoUi]::ScreenHeight, 4)
        rect = $box; kind = $Kind; fallback = $fallback }
}

# ---- preparing the clean Windows (never recorded) ----

function Install-VcRuntime($Vc) {
    # The Microsoft Visual C++ Redistributable's files and registry key, copied from the host PC that has it installed, so
    # this clean Windows is a PC with the redistributable (docs\DECISIONS.md, "Visual C++ runtime"). Only this disposable
    # sandbox's System32 changes.
    foreach ($dll in Get-ChildItem -LiteralPath $Vc.dir -Filter "*.dll") {
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $env:SystemRoot "System32") -Force
    }
    $key = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
    New-Item -Path $key -Force | Out-Null
    foreach ($pair in @(@("Installed", 1), @("Major", [int]$Vc.major), @("Minor", [int]$Vc.minor), @("Bld", [int]$Vc.bld))) {
        New-ItemProperty -Path $key -Name $pair[0] -Value $pair[1] -PropertyType DWord -Force | Out-Null
    }
    return "Visual C++ runtime $($Vc.major).$($Vc.minor).$($Vc.bld) copied from the host into System32, with its registry key"
}

function New-SecondDrive([string]$Letter, [string]$Label, [string]$Folder) {
    # A real second volume (a small virtual disk, formatted and lettered), so the folder chooser shows "Data (D:)" as a
    # PC with a second drive would. If diskpart cannot, subst gives the letter (shown as a local disk) and the log says so.
    $vhd = "C:\demo-drive.vhdx"
    $script = @("create vdisk file=`"$vhd`" maximum=2048 type=expandable", "select vdisk file=`"$vhd`"", "attach vdisk",
        "create partition primary", "format fs=ntfs quick label=`"$Label`"", "assign letter=$Letter")
    Write-Utf8 "$work\diskpart.txt" ($script -join "`r`n")
    $output = & diskpart.exe /s "$work\diskpart.txt" 2>&1 | Out-String
    $how = "a 2 GB virtual disk, NTFS, label $Label"
    if (-not (Test-Path "${Letter}:\")) {
        New-Item -ItemType Directory -Force -Path "C:\Drive-$Letter" | Out-Null
        & subst.exe "${Letter}:" "C:\Drive-$Letter"
        $how = "subst of C:\Drive-$Letter (diskpart failed: $($output.Trim() -replace '\s+', ' '))"
    }
    if ($Folder) { New-Item -ItemType Directory -Force -Path (Join-Path "${Letter}:\" $Folder) | Out-Null }
    return "drive ${Letter}: is $how$(if ($Folder) { "; made ${Letter}:\$Folder" })"
}

function Invoke-Action($Action, [string]$Label) {
    $kind = $Action.do
    switch ($kind) {
        "installSilently" {
            if (-not $job.setupExe) { throw "installSilently needs the Setup exe (demo-record.ps1 -Setup)." }
            $p = Start-Process -FilePath $job.setupExe -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=`"$out\setup-silent.log`"" -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "Setup /VERYSILENT exited $($p.ExitCode) (see setup-silent.log)." }
            if (-not (Test-Path -LiteralPath $installedExe)) { throw "Setup finished but $installedExe does not exist." }
            return "installed silently from $(Split-Path -Leaf $job.setupExe)"
        }
        "launch" {
            $path = Expand (Value $Action "path" "{installed}")
            Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path) | Out-Null
            return "started $path"
        }
        "copy" {
            $to = Expand $Action.to
            New-Item -ItemType Directory -Force -Path $to | Out-Null
            Copy-Item -LiteralPath (Expand $Action.from) -Destination $to -Force
            return "copied $(Expand $Action.from) to $to"
        }
        "copyDownloaded" {
            # A copy marked as downloaded from the internet (Zone.Identifier, zone 3), as a browser leaves it, so Windows
            # treats it as a download (SmartScreen, "Unblock").
            $to = Expand $Action.to
            New-Item -ItemType Directory -Force -Path $to | Out-Null
            $target = Join-Path $to (Split-Path -Leaf (Expand $Action.from))
            Copy-Item -LiteralPath (Expand $Action.from) -Destination $target -Force
            Set-Content -LiteralPath $target -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://github.com/kynniau/dictum/releases" -Encoding Ascii
            return "copied to $target, marked as downloaded (Zone.Identifier zone 3)"
        }
        "modelsFromHost" {
            $to = Expand (Value $Action "to" "%LOCALAPPDATA%\Dictum\models")
            New-Item -ItemType Directory -Force -Path $to | Out-Null
            foreach ($m in @($job.extraModels)) { if ($m) { Copy-Item -LiteralPath $m -Destination $to -Force } }
            return "copied $(@($job.extraModels).Count) model file(s) from the host (read-only) to $to"
        }
        "openWith" {
            # Makes `extension` (for example ".env") open in `path` for this sandbox's user, as a PC whose editor is set for that file
            # type would: a clean Windows has no handler for .env, so a button that opens such a file would show "How do you want to
            # open this file?", and the sandbox has no Notepad of its own (see environment.notepad). Per-user keys under
            # HKCU\Software\Classes only; nothing outside the disposable sandbox changes.
            $ext = [string]$Action.extension
            $exe = Expand $Action.path
            $progId = "Dictum.Demo" + $ext.TrimStart('.')
            New-Item -Path "HKCU:\Software\Classes\$ext" -Force | Out-Null
            Set-ItemProperty -Path "HKCU:\Software\Classes\$ext" -Name "(default)" -Value $progId
            New-Item -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Force | Out-Null
            Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Name "(default)" -Value "`"$exe`" `"%1`""
            return "$ext files open in $exe"
        }
        "plainDesktop" { return [DemoUi]::PlainDesktop(31, 58, 96, [bool](Value $Action "hideIcons" $true)) }
        "secondDrive" { return New-SecondDrive (Value $Action "letter" "D") (Value $Action "label" "Data") (Value $Action "folder" $null) }
        "openFolder" {
            $path = Expand $Action.path
            Start-Process explorer.exe -ArgumentList "`"$path`""
            return "opened $path in File Explorer"
        }
        "click" { return Invoke-Pointer $Action.target "click" $Label }
        "invokeClick" { return Invoke-Pointer $Action.target "invokeClick" $Label }
        "doubleClick" { return Invoke-Pointer $Action.target "doubleClick" $Label }
        "point" { return Invoke-Pointer $Action.target "point" $Label }
        "type" {
            if ($Action.PSObject.Properties["into"]) {
                $click = Invoke-Pointer $Action.into "click" $Label
                Start-Sleep -Milliseconds 300
                if (Value $Action "replace" $true) { [DemoUi]::Press("Ctrl+A", 60) }
            }
            [DemoUi]::TypeText((Expand $Action.text), $pace.typing)
            # When the typing ended, for every type action: demo-post.ps1 takes this step's still then, so it shows the typed text, not the empty field.
            $script:typedEpoch = Epoch
            if ($Action.PSObject.Properties["into"]) { $click["typedEpoch"] = Epoch; return $click }
            return "typed"
        }
        "key" { [DemoUi]::Press($Action.keys, 80); return "pressed $($Action.keys)" }
        "wait" { Start-Sleep -Milliseconds ([int]([double]$Action.seconds * 1000)); return "waited $($Action.seconds) s" }
        "waitFor" {
            $timeout = [int]([double](Value $Action.target "timeout" 60) * 1000)
            if (Value $Action "gone" $false) {
                $deadline = (Get-Date).AddMilliseconds($timeout)
                while (Find-Target $Action.target 1) { if ((Get-Date) -gt $deadline) { throw "Still there after $($timeout / 1000) s: $(Describe $Action.target)" }; Start-Sleep -Milliseconds 300 }
                return "gone: $(Describe $Action.target)"
            }
            if (-not (Find-Target $Action.target $timeout)) {
                if (Value $Action "optional" $false) { return "absent after $($timeout / 1000) s (optional): $(Describe $Action.target)" }
                $tree = Save-Tree $Action.target $Label; throw "Did not appear in $($timeout / 1000) s: $(Describe $Action.target) (tree: $(Split-Path -Leaf $tree))" }
            return "present: $(Describe $Action.target)"
        }
        default { throw "Unknown action '$kind'." }
    }
}

# ---- recording ----

function Start-InsideCapture {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $job.ffmpeg
    # gdigrab's first frame carries the wall-clock time ("start:" in its log), which places every step on the video's clock.
    $info.Arguments = "-hide_banner -y -f gdigrab -framerate $($job.fps) -draw_mouse 1 -i desktop -vf crop=trunc(iw/2)*2:trunc(ih/2)*2 " +
        "-c:v libx264 -preset ultrafast -crf 14 -pix_fmt yuv420p `"$work\raw.mp4`""
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true # "q" stops it cleanly, so the MP4 is finished
    $info.CreateNoWindow = $true
    $info.EnvironmentVariables["FFREPORT"] = "file=C\:/demo/ffmpeg.log:level=32"
    $process = [System.Diagnostics.Process]::Start($info)
    $deadline = (Get-Date).AddSeconds(20)
    while (-not ((Test-Path -LiteralPath "$work\ffmpeg.log") -and ((Get-Content -LiteralPath "$work\ffmpeg.log" -Raw) -match 'frame=\s*[1-9]'))) {
        if ($process.HasExited) { throw "ffmpeg exited at once (exit $($process.ExitCode)); see ffmpeg-inside.log." }
        if ((Get-Date) -gt $deadline) { Note "ffmpeg reported no frame within 20 s; carrying on"; break }
        Start-Sleep -Milliseconds 200
    }
    return $process
}

function Stop-InsideCapture($Process) {
    if ($Process -and -not $Process.HasExited) {
        $Process.StandardInput.Write("q")
        $Process.StandardInput.Flush()
        if (-not $Process.WaitForExit(30000)) { $Process.Kill() }
    }
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath "$work\ffmpeg.log") {
        Copy-Item -LiteralPath "$work\ffmpeg.log" -Destination "$out\ffmpeg-inside.log" -Force
        if ((Get-Content -LiteralPath "$work\ffmpeg.log" -Raw) -match 'start:\s*([0-9]+\.[0-9]+)') { $script:log.videoStartEpoch = [double]$Matches[1] }
    }
    if (Test-Path -LiteralPath "$work\raw.mp4") { Copy-Item -LiteralPath "$work\raw.mp4" -Destination "$out\raw.mp4" -Force }
}

try {
    Note "started: demo $($demo.name)"
    $ref = @("UIAutomationClient", "UIAutomationTypes", "WindowsBase")
    Add-Type -Path "C:\sbx\scripts\demo-ui.cs" -ReferencedAssemblies $ref
    [DemoUi]::Init()

    # The host sizes the sandbox window until this desktop has the recording's size: this reports the size (screen.txt)
    # until the host writes window.txt.
    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path -LiteralPath "$out\window.txt")) {
        if ((Get-Date) -gt $deadline) { Note "no window.txt from the host after 120 s: recording at the current size"; break }
        try { Write-Utf8 "$out\screen.txt" "$([DemoUi]::ScreenWidth)x$([DemoUi]::ScreenHeight)" } catch { }
        Start-Sleep -Milliseconds 500
    }
    $log.screen =[ordered]@{ width = [DemoUi]::ScreenWidth; height = [DemoUi]::ScreenHeight }
    Note "screen $([DemoUi]::ScreenWidth)x$([DemoUi]::ScreenHeight)"

    if ($job.vcRuntime) { $log.environment += Install-VcRuntime $job.vcRuntime }
    $log.environment += "OS $([Environment]::OSVersion.VersionString); user $env:USERNAME"
    $log.environment += "audio capture devices (PnP AudioEndpoint, capture): $(@(Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Microphone|Input' }).Count)"

    $i = 0
    foreach ($action in @(Value $demo "prepare" @())) {
        if (-not $action) { continue }
        $i++
        $result = Invoke-Action $action "prepare-$i"
        $text = if ($result -is [string]) { $result } else { "$($action.do) $(Describe (Value $action "target" $null))" }
        $log.prepare += "$($action.do): $text"
        Note "prepare ${i}: $text"
        Start-Sleep -Milliseconds 300
    }
    [DemoUi]::Glide([int]([DemoUi]::ScreenWidth / 2), [int]([DemoUi]::ScreenHeight / 2), 10)
    Start-Sleep -Seconds ([double](Value $demo "settle" 3))

    # Recording starts.
    $log.recordingRequestedEpoch = Epoch
    $ffmpeg = Start-InsideCapture
    Start-Sleep -Seconds ([double](Value $demo "intro" 1.5))

    $log.result = "running"
    $n = 0
    foreach ($step in @($demo.steps)) {
        $n++
        $label = "{0:00}-{1}" -f $n, (Value $step "id" "step")
        $entry = [ordered]@{ index = $n; id = (Value $step "id" $null); caption = (Value $step "caption" $null); start = $null; end = $null; typedEpoch = $null; clicks = @(); actions = @(); ok = $false; skipped = $false; error = $null }
        try {
            if ($step.PSObject.Properties["skipIf"] -and (Find-Target $step.skipIf 1500)) {
                $entry.skipped = $true; $entry.ok = $true; $entry.actions += "skipped: present $(Describe $step.skipIf)"
                $log.steps += $entry
                Note "step ${label}: skipped"
                continue
            }
            # The first pointer target is found before the caption appears, so a caption never names something not yet shown.
            $found = $null
            $first = @($step.do | Where-Object { $_.PSObject.Properties["target"] -and $_.do -in "click", "invokeClick", "doubleClick", "point" } | Select-Object -First 1)
            if ($first) {
                $wait = [int]([double](Value $first[0].target "timeout" 30) * 1000)
                $found = Find-Target $first[0].target $wait
                if (-not $found) {
                    if (Value $step "optional" $false) {
                        $entry.skipped = $true; $entry.ok = $true; $entry.actions += "optional, not shown: $(Describe $first[0].target)"
                        $log.steps += $entry
                        Note "step ${label}: optional target absent"
                        continue
                    }
                }
            }
            # The step starts (its caption shows) now: a "scrollFirst" scroll comes after this, so the caption changes first and
            # the old caption never sits over the scroll (lens on 7900e49).
            $entry.start = Epoch
            if ($first -and $found -and (Value $step "scrollFirst" $false)) {
                # "scrollFirst": scroll the target into view as the caption appears, before the lead pause, so a caption about text
                # further down a page is read with that text on screen.
                [void][DemoUi]::EnsureVisible($found, [int]($pace.glide * 1000))
                $entry.actions += "scrolled into view as the caption appeared: $(Describe $first[0].target)"
            }
            $script:typedEpoch = $null
            Start-Sleep -Milliseconds ([int]([double](Value $step "lead" $pace.lead) * 1000))
            foreach ($action in @($step.do)) {
                if (-not $action) { continue }
                $result = Invoke-Action $action $label
                if ($result -is [System.Collections.IDictionary]) { $entry.clicks += $result; $entry.actions += "$($action.do) $(Describe (Value $action "target" (Value $action "into" $null)))" }
                else { $entry.actions += "$($action.do): $result" }
            }
            $entry.typedEpoch = $script:typedEpoch
            Start-Sleep -Milliseconds ([int]([double](Value $step "after" $pace.after) * 1000))
            $entry.ok = $true
        } catch {
            $entry.error = $_.Exception.Message
            Note "step $label failed: $($_.Exception.Message)"
            # Whether Dictum itself is stuck (a hung window looks like a missing target): its state, for the run's log.
            try { Note ("Dictum at the failure: " + ((Get-Process -Name Dictum -ErrorAction SilentlyContinue | ForEach-Object { "pid $($_.Id) responding=$($_.Responding) threads=$($_.Threads.Count) cpu=$([math]::Round($_.CPU, 1)) s" }) -join "; ")) } catch { }
        }
        $entry.end = Epoch
        $log.steps += $entry
        Note "step ${label}: $(if ($entry.ok) { 'ok' } else { 'FAILED' }) $($entry.caption)"
        if (-not $entry.ok) { break }
    }
    $failed = @($log.steps | Where-Object { -not $_.ok })
    $log.result = if ($failed) { "failed at step $($failed[0].index)" } elseif ($n -lt @($demo.steps).Count) { "stopped" } else { "complete" }
    Start-Sleep -Seconds ([double](Value $demo "outro" 2.5))
} catch {
    $log.result = "error: $($_.Exception.Message)"
    Note "error: $($_.Exception.Message)"
} finally {
    $log.endEpoch = Epoch
    try { Stop-InsideCapture $ffmpeg } catch { Note "stopping ffmpeg: $($_.Exception.Message)" }
    Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA "Dictum\logs") -Destination "$out\dictum-logs" -Recurse -ErrorAction SilentlyContinue
    # The settings folder's files (names, sizes, and the two JSON files; .env is listed, never copied), for the run's record of what the demo changed.
    try {
        $cfg = Join-Path $env:APPDATA "Dictum"
        Get-ChildItem -LiteralPath $cfg -Force -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)`t$($_.Length)" } | Set-Content -LiteralPath "$out\config-listing.txt" -Encoding UTF8
        foreach ($f in "settings.json", "consent.json") { if (Test-Path -LiteralPath (Join-Path $cfg $f)) { Copy-Item -LiteralPath (Join-Path $cfg $f) -Destination "$out\config-$f" -Force } }
    } catch { }
    Write-Utf8 "$out\steps.json" ($log | ConvertTo-Json -Depth 8)
    Note "done: $($log.result)"
    Write-Utf8 "$out\done.txt" $log.result
    & shutdown.exe /s /t 3
}
