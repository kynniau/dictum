<#
.SYNOPSIS
  Per-user install of Dictum (no admin rights): %LOCALAPPDATA%\Programs\Dictum plus a Start Menu shortcut.

.DESCRIPTION
  Copies a published build (the dist\Dictum folder or a Dictum-<version>-win-x64.zip) into
  %LOCALAPPDATA%\Programs\Dictum, replacing an older copy, and creates Start Menu\Programs\Dictum.lnk.
  Runs unattended, so it works from a remote PowerShell session or Intune. Prints what it did.
  Never overwrites an existing settings.json or .env in %APPDATA%\Dictum unless -Force is given. A .env placed beside
  Dictum.exe (a documented secrets location) is user data: an upgrade leaves it where it is, -Force included.

.PARAMETER Source
  dist\Dictum folder or the zip. Default: ..\dist\Dictum next to this script, else the newest
  ..\dist\Dictum-*-win-x64.zip, else this script's own folder if it contains Dictum.exe.
.PARAMETER LaunchAtLogin
  Adds HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Dictum (the same value the tray item writes).
.PARAMETER Model
  Pre-downloads this model (e.g. base.en) with Dictum.exe --download-model. If no settings.json exists and
  -Settings is not given, also writes it as whisper.model so the first launch skips the speech model picker (the
  cleanup picker still asks, unless settings.json names llm.model or llm.provider).
.PARAMETER ModelsFrom
  A folder (for example a network share) whose .bin and .gguf model files are copied into the models folder instead
  of downloading them: for air-gapped or offline_only installs. The folder is the one the installed app resolves
  (whisper.models_dir from the seeded settings.json, else %LOCALAPPDATA%\Dictum\models). Files already present with
  the same size are skipped.
.PARAMETER Settings
  A prepared settings.json to seed %APPDATA%\Dictum\settings.json.
.PARAMETER EnvFile
  A prepared .env to copy to %APPDATA%\Dictum\.env (or set ANTHROPIC_API_KEY machine-wide instead).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1 -Source .\Dictum-1.0.0-win-x64.zip -Model base.en -LaunchAtLogin
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$LaunchAtLogin,
    [string]$Model,
    [string]$Settings,
    [string]$EnvFile,
    [string]$ModelsFrom,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
function Say([string]$message) { Write-Host "  - $message" }

$installDir = Join-Path $env:LOCALAPPDATA "Programs\Dictum"
$configDir = Join-Path $env:APPDATA "Dictum"
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Dictum.lnk"
$exe = Join-Path $installDir "Dictum.exe"

if ($Model -and ($Model -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $Model.Contains(".."))) { throw "Invalid model name '$Model'." }
foreach ($file in @($Settings, $EnvFile)) { if ($file -and -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "File not found: $file" } }
if ($ModelsFrom -and -not (Test-Path -LiteralPath $ModelsFrom -PathType Container)) { throw "Folder not found: $ModelsFrom" }

# 1. Find the build.
if (-not $Source) {
    $repoDist = Join-Path (Split-Path -Parent $PSScriptRoot) "dist"
    $zip = Get-ChildItem -Path $repoDist -Filter "Dictum-*-win-x64.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (Test-Path (Join-Path $repoDist "Dictum\Dictum.exe")) { $Source = Join-Path $repoDist "Dictum" }
    elseif ($zip) { $Source = $zip.FullName }
    elseif (Test-Path (Join-Path $PSScriptRoot "Dictum.exe")) { $Source = $PSScriptRoot }
    else { throw "No build found. Pass -Source <dist\Dictum folder or Dictum-<version>-win-x64.zip>." }
}
$Source = (Resolve-Path -LiteralPath $Source).Path
Write-Host "Installing Dictum from $Source"

# The earlier build was named Dictate. Left installed, it may start at login beside Dictum and hold its files open, so
# Dictum could not move them (SPEC section 21: Dictum moves those settings, models and recordings on its first start).
$legacyExe = Join-Path $env:LOCALAPPDATA "Programs\Dictate\Dictate.exe"
if (Test-Path -LiteralPath $legacyExe) {
    Write-Warning "The earlier build, Dictate, is installed ($legacyExe). Uninstall it with its own uninstall.ps1 -KeepData, then start Dictum: Dictate's settings, models and recordings move to Dictum on its first start."
}

$staging = $null
if ((Get-Item -LiteralPath $Source).PSIsContainer) {
    $from = $Source
} else {
    $staging = Join-Path $env:TEMP ("dictate-install-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive -LiteralPath $Source -DestinationPath $staging
    $from = $staging
}
if (-not (Test-Path (Join-Path $from "Dictum.exe"))) { throw "Dictum.exe not found in $Source." }

$exeEnv = Join-Path $installDir ".env"   # a documented secrets location, i.e. user data: kept across upgrades
try {
    # 2. Stop a running copy of this install so its files can be replaced.
    $running = Get-Process -Name Dictum -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500; Say "stopped the running Dictum" }

    # 3. Copy the program (replacing an older copy, but only a folder that is ours). The .env beside the exe stays where
    #    it is, -Force included: it is never copied elsewhere (a killed script must not leave a key in %TEMP%).
    if (Test-Path $installDir) {
        $isOurs = (Test-Path (Join-Path $installDir "Dictum.exe")) -or -not (Get-ChildItem -LiteralPath $installDir -Force | Where-Object Name -ne ".env" | Select-Object -First 1)
        if (-not $isOurs) { throw "$installDir exists and does not look like a Dictum install; not touching it." }
        Get-ChildItem -LiteralPath $installDir -Force | Where-Object Name -ne ".env" | Remove-Item -Recurse -Force
        if (Test-Path -LiteralPath $exeEnv) { Say "kept $exeEnv (secrets beside the exe are user data)" }
    }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Get-ChildItem -LiteralPath $from -Force | Where-Object Name -ne ".env" | Copy-Item -Destination $installDir -Recurse -Force
    $version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    Say "program $version copied to $installDir"
} finally {
    if ($staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

# 4. Start Menu shortcut.
New-Item -ItemType Directory -Force -Path (Split-Path $shortcutPath) | Out-Null
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcutPath)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.Description = "Dictum - voice dictation"
$link.Save()
Say "Start Menu shortcut: $shortcutPath"

# 5. Settings and secrets (never overwritten without -Force).
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$settingsTarget = Join-Path $configDir "settings.json"
if ($Settings) {
    if ((Test-Path $settingsTarget) -and -not $Force) { Say "kept existing $settingsTarget (use -Force to replace)" }
    else {
        Copy-Item -LiteralPath $Settings -Destination $settingsTarget -Force
        # A seed copied from a read-only share stays read-only, and the tray could then never save a choice.
        (Get-Item -LiteralPath $settingsTarget).IsReadOnly = $false
        Say "settings.json seeded from $Settings"
    }
}
if ($EnvFile) {
    $envTarget = Join-Path $configDir ".env"
    if ((Test-Path $envTarget) -and -not $Force) { Say "kept existing $envTarget (use -Force to replace)" }
    else { Copy-Item -LiteralPath $EnvFile -Destination $envTarget -Force; Say ".env copied to $envTarget" }
}

# 5b. Models copied from a folder (no network): .bin speech models and .gguf cleanup models, into the folder the installed
#     app itself resolves (whisper.models_dir in the settings just seeded, else %LOCALAPPDATA%\Dictum\models).
if ($ModelsFrom) {
    $modelsDir = (& $exe --print-models-dir | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $modelsDir) { throw "Dictum.exe --print-models-dir failed (exit $LASTEXITCODE): fix settings.json, then run the install again. No models were copied." }
    New-Item -ItemType Directory -Force -Path $modelsDir | Out-Null
    $copied = 0
    foreach ($f in Get-ChildItem -LiteralPath $ModelsFrom -File | Where-Object { $_.Extension -in ".bin", ".gguf" }) {
        $dest = Join-Path $modelsDir $f.Name
        if ((Test-Path $dest) -and (Get-Item $dest).Length -eq $f.Length) { Say "model $($f.Name) already present (same size), skipped"; continue }
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        Say ("model {0} copied ({1:N0} bytes)" -f $f.Name, $f.Length)
        $copied++
    }
    Say "$copied model file(s) copied from $ModelsFrom; nothing downloaded"
}

# 6. Model pre-download through the app itself (same catalogue, host and checks). Piping makes PowerShell wait.
#    Windows PowerShell 5.1 turns a native program's stderr lines into errors under 2>&1, which "Stop" would make
#    fatal on the first warning, so the preference is relaxed for this call and the exit code checked instead.
if ($Model) {
    Say "downloading model $Model ..."
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $exe --download-model $Model 2>&1 | ForEach-Object { Write-Host "    $_" }
        $downloadExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($downloadExit -ne 0) { throw "Model download failed (exit $downloadExit). settings.json was not changed and launch at login was not set." }
    if (-not $Settings -and -not (Test-Path $settingsTarget)) {
        Set-Content -LiteralPath $settingsTarget -Encoding UTF8 -Value ('{ "whisper": { "model": "' + $Model + '" } }')
        Say "settings.json created with whisper.model = $Model (all other settings default)"
    }
}

# 7. Launch at login (same Run value the tray menu writes).
if ($LaunchAtLogin) {
    $run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-ItemProperty -Path $run -Name "Dictum" -Value "`"$exe`"" -PropertyType String -Force | Out-Null
    Say "launch at login enabled (HKCU Run\Dictum)"
}

Write-Host "Done. Start Dictum from the Start Menu, or run: & `"$exe`""
Write-Host "Check it with: & `"$exe`" --selftest | Out-Host"
