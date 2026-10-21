<#
.SYNOPSIS
  Records Dictum walkthrough videos unattended in Windows Sandbox: one sandbox per demo, driven step by step from a data
  file in scripts\demos, then captioned by demo-post.ps1 into dist\demos\<name>.mp4, .gif and -poster.png.

.DESCRIPTION
  For each -Demo, this script writes a .wsb (from demo-record.wsb, into the staging folder in %TEMP%, so the run folder
  never holds a host path) that maps, read-only, the scripts folder, a staged copy of the Setup exe, the published folder
  (-App, when given), a staged folder holding copies of only the extra models the demo names (never the host's whole
  model folder), the Visual C++ runtime files from this PC's System32 and ffmpeg; and, writable, the run's own folder.
  It starts the sandbox, sizes its window so the sandbox desktop is -Width x -Height, and waits for the driver
  (demo-inside.ps1, run hidden at logon) to write done.txt. The driver prepares the clean Windows, records the steps and
  shuts the sandbox down; this script closes it if it is still open after a minute.

  ffmpeg's gdigrab (the static build, mapped read-only) records the sandbox's own desktop, inside the sandbox, so nothing
  on the host can appear in a video and the host PC stays usable while a demo records. Recording the sandbox window
  from the host was tried on 21/10/2026 and dropped: gdigrab -i "title=Windows Sandbox" recorded a plain white frame
  (the window draws through DirectComposition), and recording the screen area under the window recorded whatever host
  window covered it, private content included (docs\DEMOS.md, "Why the recording is made inside the sandbox").

  The sandbox gets the Visual C++ runtime from this PC (DLLs and registry key), so it is a PC with the redistributable
  installed, as Dictum needs (docs\DECISIONS.md, "Visual C++ runtime"); -NoVcRuntime records a PC without it.

  Needs Windows Sandbox, ffmpeg (winget install Gyan.FFmpeg), 100 % display scaling on the host (the sandbox's title bar
  is taken as 33 px), a host screen that fits a Width x (Height + 33) window plus borders (1600 x 933 by default: a
  1366 x 768 screen cannot), and no other sandbox open. Changes nothing on the host except the run folders, dist\demos
  and a staging folder in %TEMP% that is deleted afterwards.

.PARAMETER Demo
  Demo names (scripts\demos\<name>.json) or paths, comma-separated. Default: every file in scripts\demos.
.PARAMETER Setup
  The Setup exe a demo installs from. Default: the newest dist\Dictum-Setup-*.exe (scripts\build-installer.ps1).
.PARAMETER App
  A published folder holding Dictum.exe ({app} in a demo file). Optional.
.PARAMETER ModelsDir
  Where a demo's environment.extraModels are read from. Only the files a demo names are copied (to the staging folder,
  which is mapped read-only); the folder itself is never mapped. Default: %LOCALAPPDATA%\Dictum\models.
.PARAMETER Width
  The sandbox desktop's width in pixels. Default 1600.
.PARAMETER Height
  The sandbox desktop's height in pixels. Default 900.
.PARAMETER RunsDir
  Where each run's folder (job.json, steps.json, raw.mp4, logs) is made. Default: dist\demos\runs.
.PARAMETER SizeAttempts
  How many times a sandbox that came up at another desktop size is started (in all, counting the first) before the
  script gives up. Default 6.
.PARAMETER AllowOtherSize
  When the attempts run out and the sandbox desktop is still not Width x Height, record at the size it has (with a
  warning) instead of throwing.
.PARAMETER SkipDpiCheck
  Do not refuse to run when the host's display scaling is not 100 %. The title bar height is then a guess, so the
  sandbox desktop may never reach Width x Height.
.PARAMETER NoPost
  Record only; run demo-post.ps1 later.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-record.ps1 -Demo change-model
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\demo-record.ps1 -Demo install,model-folder -NoPost
#>
[CmdletBinding()]
param(
    [string[]]$Demo = @(),
    [string]$Setup,
    [string]$App,
    [string]$ModelsDir = (Join-Path $env:LOCALAPPDATA "Dictum\models"),
    [int]$Width = 1600,
    [int]$Height = 900,
    [string]$RunsDir,
    [string]$Ffmpeg,
    [switch]$NoVcRuntime,
    [switch]$NoPost,
    [switch]$AllowOtherSize,
    [switch]$SkipDpiCheck,
    [int]$Fps = 30,
    [int]$SizeAttempts = 6,
    [int]$TimeoutMinutes = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "demo-common.ps1")
$Demo = @($Demo | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Demo) {
    # Every recorded demo; stills walkthroughs (kind "stills") are built by demo-stills.ps1.
    $Demo = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "demos") -Filter "*.json" |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties["kind"] -eq $null } | ForEach-Object BaseName)
}
$sandbox = Join-Path $env:SystemRoot "System32\WindowsSandbox.exe"
if (-not (Test-Path -LiteralPath $sandbox)) { throw "Windows Sandbox is not installed: turn on the Windows feature ""Windows Sandbox"" first." }
$Ffmpeg = Find-Ffmpeg $Ffmpeg
if (-not $Ffmpeg -or -not (Test-Path -LiteralPath $Ffmpeg)) { throw "ffmpeg.exe was not found: winget install Gyan.FFmpeg, or pass -Ffmpeg." }
# WinGet's Links entry is a symbolic link; the sandbox needs the folder of the real (static) ffmpeg.exe.
$item = Get-Item -LiteralPath $Ffmpeg
$ffmpegReal = if ($item.LinkType -and $item.Target) { @($item.Target)[0] } else { $item.FullName }
if (-not $Setup) {
    $Setup = Get-ChildItem -Path (Join-Path $root "dist") -Filter "Dictum-Setup-*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $RunsDir) { $RunsDir = Join-Path $root "dist\demos\runs" }

Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class DemoHostWindow {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    // The primary monitor's effective DPI (96 = 100 % scaling), or 0 when Windows cannot say. powershell.exe is DPI-unaware
    // and Windows tells an unaware process 96 whatever the scaling, so this thread is made per-monitor-v2 aware (context
    // -4) for the one call and put back afterwards.
    public static int PrimaryMonitorDpi() {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            uint x, y;
            return GetDpiForMonitor(MonitorFromWindow(GetDesktopWindow(), 1), 0, out x, out y) == 0 ? (int)x : 0;
        } finally {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }
    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            var s = new StringBuilder(256); GetWindowText(h, s, 256);
            if (p == pid && IsWindowVisible(h) && s.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# Display scaling: the window sizing below assumes the sandbox's title bar is 33 px, which holds at 100 % scaling only.
# Two reads, either of which stops the run: the primary monitor's effective DPI (shcore GetDpiForMonitor, asked as a
# per-monitor-aware thread because this process is DPI-unaware and would be told 96 whatever the scaling; 96 is 100 %) and
# the registry value HKCU:\Control Panel\Desktop\WindowMetrics\AppliedDPI, the DPI Windows applied to the desktop (read
# when present). Only a PC at 100 % has been tried (both reads gave 96); a scaled host has not been tested.
# -SkipDpiCheck exists for a wrong refusal.
if (-not $SkipDpiCheck) {
    $monitorDpi = [DemoHostWindow]::PrimaryMonitorDpi()
    $applied = (Get-ItemProperty -Path "HKCU:\Control Panel\Desktop\WindowMetrics" -ErrorAction SilentlyContinue)
    $appliedDpi = if ($applied -and $applied.PSObject.Properties["AppliedDPI"]) { [int]$applied.AppliedDPI } else { 0 }
    if (($monitorDpi -ne 0 -and $monitorDpi -ne 96) -or ($appliedDpi -ne 0 -and $appliedDpi -ne 96)) {
        throw "demo-record.ps1 needs 100 % display scaling on the host (title bar assumed 33 px); this PC reports monitor DPI $monitorDpi, registry AppliedDPI $(if ($appliedDpi) { $appliedDpi } else { 'unset' }). Set Settings > System > Display > Scale to 100 %, or pass -SkipDpiCheck."
    }
}

function Close-Sandbox([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Process -Name "WindowsSandboxRemoteSession", "WindowsSandboxClient" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    $left = @(Get-Process -Name "WindowsSandboxRemoteSession", "WindowsSandboxClient" -ErrorAction SilentlyContinue)
    if ($left) {
        Write-Host "  The sandbox did not shut itself down within $Seconds s: closing its window."
        $left | Stop-Process -Force
    }
    # Every sandbox process (the server lingers a few seconds after the window), so the next demo's check finds none.
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process -Name "WindowsSandbox*", "vmmemWindowsSandbox" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
}

# The Visual C++ runtime this PC has installed, for the sandbox (docs\DECISIONS.md, "Visual C++ runtime").
$vcInfo = $null
if (-not $NoVcRuntime) {
    $key = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
    $dlls = @("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll") | ForEach-Object { Join-Path $env:SystemRoot "System32\$_" }
    if (-not $key -or ($dlls | Where-Object { -not (Test-Path -LiteralPath $_) })) {
        throw "This PC has no complete Microsoft Visual C++ Redistributable (x64) to lend the sandbox; install it or pass -NoVcRuntime."
    }
    $vcInfo = @{ Dlls = $dlls; Major = $key.Major; Minor = $key.Minor; Bld = $key.Bld }
}

$results = @()
foreach ($name in $Demo) {
    $file = if (Test-Path -LiteralPath $name) { (Resolve-Path -LiteralPath $name).Path } else { Join-Path $PSScriptRoot "demos\$name.json" }
    if (-not (Test-Path -LiteralPath $file)) { throw "No demo file $file." }
    $data = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.PSObject.Properties["kind"]) { throw "$name is a stills walkthrough (kind $($data.kind)): build it with demo-stills.ps1." }
    if (Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue) { throw "A Windows Sandbox is already open; close it first (only one runs at a time)." }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $run = Join-Path $RunsDir "$stamp-$($data.name)"
    New-Item -ItemType Directory -Force -Path $run | Out-Null
    $run = (Resolve-Path -LiteralPath $run).Path
    $stage = Join-Path $env:TEMP "dictum-demo-stage-$stamp"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    # From here on, anything that throws (a missing extra model, say) still deletes the staging folder and closes the sandbox.
    try {
        $maps = [ordered]@{ "C:\sbx\scripts" = $PSScriptRoot }
        $job = [ordered]@{ demo = $data; demoFile = (Split-Path -Leaf $file); capture = "inside"; fps = $Fps
            setupExe = $null; app = $null; ffmpeg = $null; vcRuntime = $null; extraModels = @(); target = @{ width = $Width; height = $Height } }
        $usesSetup = ($data | ConvertTo-Json -Depth 20) -match '"installSilently"|\{setup\}'
        if ($usesSetup) {
            if (-not $Setup -or -not (Test-Path -LiteralPath $Setup)) { throw "$($data.name) installs Dictum: build dist\Dictum-Setup-<version>.exe (scripts\build-installer.ps1) or pass -Setup." }
            New-Item -ItemType Directory -Force -Path "$stage\setup" | Out-Null
            Copy-Item -LiteralPath $Setup -Destination "$stage\setup" # staged: the run folder (under dist) is mapped writable, dist itself is not mapped
            $maps["C:\sbx\setup"] = "$stage\setup"
            $job.setupExe = "C:\sbx\setup\" + (Split-Path -Leaf $Setup)
        }
        if ($App) {
            if (-not (Test-Path -LiteralPath (Join-Path $App "Dictum.exe"))) { throw "$App holds no Dictum.exe." }
            $maps["C:\sbx\app"] = (Resolve-Path -LiteralPath $App).Path
            $job.app = "C:\sbx\app"
        }
        $extra = @(if ($data.PSObject.Properties["environment"] -and $data.environment.PSObject.Properties["extraModels"]) { $data.environment.extraModels })
        if ($extra) {
            foreach ($m in $extra) { if (-not (Test-Path -LiteralPath (Join-Path $ModelsDir $m))) { throw "$($data.name) needs $m in $ModelsDir (no downloads)." } }
            # Only the files the demo names, copied to the staging folder, which is what the sandbox sees (read-only): the
            # host's whole model folder is never mapped. Names are the files' leaf names, so the sandbox paths stay flat.
            New-Item -ItemType Directory -Force -Path "$stage\models" | Out-Null
            foreach ($m in $extra) { Copy-Item -LiteralPath (Join-Path $ModelsDir $m) -Destination "$stage\models" }
            $maps["C:\sbx\models"] = "$stage\models"
            $job.extraModels = @($extra | ForEach-Object { "C:\sbx\models\" + (Split-Path -Leaf $_) })
        }
        if ($vcInfo) {
            New-Item -ItemType Directory -Force -Path "$stage\vcrt" | Out-Null
            $vcInfo.Dlls | ForEach-Object { Copy-Item -LiteralPath $_ -Destination "$stage\vcrt" }
            $maps["C:\sbx\vcrt"] = "$stage\vcrt"
            $job.vcRuntime = [ordered]@{ dir = "C:\sbx\vcrt"; major = $vcInfo.Major; minor = $vcInfo.Minor; bld = $vcInfo.Bld }
        }
        $maps["C:\sbx\ffmpeg"] = Split-Path -Parent $ffmpegReal
        $job.ffmpeg = "C:\sbx\ffmpeg\" + (Split-Path -Leaf $ffmpegReal)
        Write-Utf8 (Join-Path $run "job.json") ($job | ConvertTo-Json -Depth 20)

        $mapped = foreach ($inside in $maps.Keys) {
            "    <MappedFolder><HostFolder>{0}</HostFolder><SandboxFolder>{1}</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" -f
                [Security.SecurityElement]::Escape($maps[$inside]), $inside
        }
        $mapped += "    <MappedFolder><HostFolder>{0}</HostFolder><SandboxFolder>C:\sbx\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" -f
            [Security.SecurityElement]::Escape($run)
        $template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "demo-record.wsb")) -replace '(?s)^\s*<!--.*?-->\s*', ''
        $wsb = Join-Path $stage "demo.wsb" # not in $run: the sandbox can write to $run, and demo.wsb lists host paths
        Write-Utf8 $wsb $template.Replace("{{MappedFolders}}", ($mapped -join "`r`n"))

        Write-Host "Recording $($data.name) in Windows Sandbox; run folder $run"
        $maps.GetEnumerator() | ForEach-Object { Write-Host ("  {0} <- {1} (read-only)" -f $_.Key, $_.Value) }
        # The sandbox desktop takes its size from the window when the sandbox connects. Resizing the window later (even
        # with the end of a drag, WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE, sent) left the desktop at the window's earlier size
        # in some runs on 21/10/2026, so the window is held at Width x Height from the moment it appears, and if the
        # desktop the driver reports (screen.txt) is still another size 20 s later, the sandbox is closed and started
        # again, up to -SizeAttempts times (inside the sandbox, ChangeDisplaySettings returns success and changes nothing,
        # observed 21/10/2026). The client area includes the sandbox's own title bar (33 px at 100 % scaling,
        # observed 21/10/2026). steps.json records the size that resulted.
        $titleBar = 33 # 100 % scaling only: checked above
        $wanted = "${Width}x${Height}"
        $r = New-Object DemoHostWindow+RECT; $c = New-Object DemoHostWindow+RECT
        $screenFile = Join-Path $run "screen.txt"
        $doneFile = Join-Path $run "done.txt"
        for ($attempt = 1; ; $attempt++) {
            # Everything the previous attempt's driver left: a stale done.txt or steps.json would read as a finished run.
            Remove-Item -LiteralPath $screenFile, (Join-Path $run "progress.txt"), $doneFile, (Join-Path $run "steps.json"), (Join-Path $run "raw.mp4") -ErrorAction SilentlyContinue
            Start-Process -FilePath $sandbox -ArgumentList "`"$wsb`""
            $deadline = (Get-Date).AddSeconds(120)
            $window = [IntPtr]::Zero
            while ($window -eq [IntPtr]::Zero) {
                if ((Get-Date) -gt $deadline) { throw "The Windows Sandbox window did not appear within 120 s." }
                Start-Sleep -Milliseconds 100
                foreach ($p in @(Get-Process -Name "WindowsSandboxRemoteSession", "WindowsSandboxClient" -ErrorAction SilentlyContinue)) {
                    $h = [DemoHostWindow]::Find([uint32]$p.Id, "Windows Sandbox")
                    if ($h -ne [IntPtr]::Zero) { $window = $h }
                }
            }
            $matched = 0; $nudged = [datetime]::MinValue; $reported = ""; $wrongSince = $null
            $deadline = (Get-Date).AddSeconds(120)
            while ($matched -lt 2 -and (Get-Date) -lt $deadline) {
                if (Test-Path -LiteralPath $doneFile) { throw "The driver ended before recording: $((Get-Content -LiteralPath $doneFile -Raw).Trim())" }
                [void][DemoHostWindow]::GetWindowRect($window, [ref]$r); [void][DemoHostWindow]::GetClientRect($window, [ref]$c)
                $reported = if (Test-Path -LiteralPath $screenFile) { "$(Get-Content -LiteralPath $screenFile -Raw -ErrorAction SilentlyContinue)".Trim() } else { "" }
                if ($reported -eq $wanted) { $matched++ } else {
                    $matched = 0
                    if ($reported -and -not $wrongSince) { $wrongSince = Get-Date }
                    if ($wrongSince -and ((Get-Date) - $wrongSince).TotalSeconds -gt 20) { break }
                    $clientWrong = $c.R -ne $Width -or $c.B -ne $Height + $titleBar
                    if ($clientWrong -or ($reported -and ((Get-Date) - $nudged).TotalSeconds -gt 6)) {
                        [void][DemoHostWindow]::ShowWindow($window, 9) # SW_RESTORE: a maximised window ignores the size
                        [void][DemoHostWindow]::SendMessage($window, 0x0231, [IntPtr]::Zero, [IntPtr]::Zero) # WM_ENTERSIZEMOVE
                        [void][DemoHostWindow]::SetWindowPos($window, [IntPtr]::Zero, 40, 40, $Width + ($r.R - $r.L) - $c.R, $Height + $titleBar + ($r.B - $r.T) - $c.B, 0x0040)
                        [void][DemoHostWindow]::SendMessage($window, 0x0232, [IntPtr]::Zero, [IntPtr]::Zero) # WM_EXITSIZEMOVE
                        $nudged = Get-Date
                    }
                }
                Start-Sleep -Milliseconds 150
            }
            if ($reported -eq $wanted -or $attempt -ge $SizeAttempts) { break }
            Write-Host "  The sandbox desktop is '$reported', not ${wanted}: starting the sandbox again (attempt $($attempt + 1) of $SizeAttempts)."
            Get-Process -Name "WindowsSandboxRemoteSession", "WindowsSandboxClient" -ErrorAction SilentlyContinue | Stop-Process -Force
            Close-Sandbox 1
        }
        if ($reported -ne $wanted -and -not $AllowOtherSize) {
            throw "The sandbox desktop is '$reported', not ${wanted}, after $attempt attempt(s): not recording. The host screen must fit a $Width x $($Height + $titleBar) window plus its borders (a 1366 x 768 screen cannot); or pass -AllowOtherSize to record at the size it got, or raise -SizeAttempts."
        }
        [void][DemoHostWindow]::SetForegroundWindow($window)
        [void][DemoHostWindow]::GetClientRect($window, [ref]$c)
        Write-Utf8 (Join-Path $run "window.txt") "client $($c.R)x$($c.B); sandbox desktop $reported; attempt $attempt"
        if ($reported -ne $wanted) { Write-Warning "The sandbox desktop is '$reported', not ${wanted}: recording at that size (-AllowOtherSize)." }
        Write-Host "  Sandbox window client area $($c.R)x$($c.B); sandbox desktop $reported (attempt $attempt)."

        $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
        while (-not (Test-Path -LiteralPath (Join-Path $run "done.txt"))) {
            if ((Get-Date) -gt $deadline) { throw "No done.txt after $TimeoutMinutes minutes; see $run\progress.txt." }
            Start-Sleep -Milliseconds 250
        }
        Write-Host "  $((Get-Content -LiteralPath (Join-Path $run 'done.txt') -Raw).Trim())"
    } finally {
        Close-Sandbox 60
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    $results += [pscustomobject]@{ Demo = $data.name; Run = $run; File = $file }
}

if (-not $NoPost) {
    foreach ($r in $results) {
        if (-not (Test-Path -LiteralPath (Join-Path $r.Run "raw.mp4"))) { Write-Host "$($r.Demo): no raw.mp4 in $($r.Run); nothing to caption."; continue }
        & (Join-Path $PSScriptRoot "demo-post.ps1") -Run $r.Run -Demo $r.File -Ffmpeg $Ffmpeg
    }
}
$results | Format-Table -AutoSize | Out-String | Write-Host
