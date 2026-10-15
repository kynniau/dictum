<#
.SYNOPSIS
  Removes a per-user Dictum install: program files, Start Menu shortcut and the HKCU Run value.

.DESCRIPTION
  User data is models, logs and calibration recordings (%LOCALAPPDATA%\Dictum), settings, .env, dictionary.txt,
  corrections.txt and any profanity.txt override (%APPDATA%\Dictum), and a .env placed
  beside Dictum.exe (a documented secrets location):
    -KeepData    keep all of it (the exe-folder .env stays in %LOCALAPPDATA%\Programs\Dictum)
    -RemoveData  remove it without asking (for unattended runs)
    neither      ask in an interactive console; in a non-interactive session (Intune, remoting) keep it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File uninstall.ps1 -KeepData
#>
[CmdletBinding()]
param(
    [switch]$KeepData,
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
function Say([string]$message) { Write-Host "  - $message" }
if ($KeepData -and $RemoveData) { throw "Use either -KeepData or -RemoveData, not both." }

$installDir = Join-Path $env:LOCALAPPDATA "Programs\Dictum"
$exeEnv = Join-Path $installDir ".env"
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Dictum.lnk"
$dataDirs = @((Join-Path $env:LOCALAPPDATA "Dictum"), (Join-Path $env:APPDATA "Dictum")) | Where-Object { Test-Path $_ }
$hasExeEnv = Test-Path -LiteralPath $exeEnv

Write-Host "Uninstalling Dictum"

# Decide about user data first, so nothing is deleted before the question is answered.
$remove = [bool]$RemoveData
if (-not $KeepData -and -not $RemoveData -and ($dataDirs -or $hasExeEnv)) {
    $what = @($dataDirs) + @(if ($hasExeEnv) { $exeEnv })
    $interactive = [Environment]::UserInteractive -and $Host.Name -eq "ConsoleHost" -and -not $PSSenderInfo
    if ($interactive) {
        try {
            $answer = Read-Host "Also delete models, logs, settings, secrets, your dictionary and corrections, and calibration recordings in $($what -join ', ')? [y/N]"
        } catch {
            # powershell -NonInteractive still looks interactive above, but Read-Host throws there.
            Say "cannot ask in this session: keeping user data (re-run with -RemoveData to delete it)"
            $answer = ""
        }
        $remove = $answer -match '^(y|yes)$'
    } else {
        Say "non-interactive session: keeping user data (re-run with -RemoveData to delete it)"
    }
}

$running = Get-Process -Name Dictum -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500; Say "stopped the running Dictum" }

if (Test-Path $installDir) {
    # Ours if it holds Dictum.exe, or only the .env a previous -KeepData uninstall left behind.
    $isOurs = (Test-Path (Join-Path $installDir "Dictum.exe")) -or -not (Get-ChildItem -LiteralPath $installDir -Force | Where-Object Name -ne ".env" | Select-Object -First 1)
    if (-not $isOurs) { throw "$installDir does not look like a Dictum install; not touching it." }
    if ($hasExeEnv -and -not $remove) {
        # Remove the program but leave the user's .env where a reinstall will find it.
        Get-ChildItem -LiteralPath $installDir -Force | Where-Object Name -ne ".env" | Remove-Item -Recurse -Force
        Say "removed the program from $installDir; kept $exeEnv"
    } else {
        Remove-Item -LiteralPath $installDir -Recurse -Force
        Say "removed $installDir"
    }
} else { Say "no program folder at $installDir" }

if (Test-Path $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force; Say "removed $shortcutPath" }

$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValue = (Get-ItemProperty -Path $run -Name "Dictum" -ErrorAction SilentlyContinue).Dictum
if ($runValue) {
    # Only remove the value if it starts this install, never another copy of Dictum.
    if ($runValue.Trim().Trim([char]34) -ieq (Join-Path $installDir "Dictum.exe")) {
        Remove-ItemProperty -Path $run -Name "Dictum"
        Say "removed launch at login (HKCU Run\Dictum)"
    } else {
        Say "left HKCU Run\Dictum alone: it points at $runValue, not this install"
    }
}

foreach ($dir in $dataDirs) {
    if ($remove) { Remove-Item -LiteralPath $dir -Recurse -Force; Say "removed $dir" }
    else { Say "kept $dir" }
}
Write-Host "Done."
