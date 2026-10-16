<#
.SYNOPSIS
  Builds dist\Dictum-Setup-<version>.exe, Dictum's per-user direct-download installer (SPEC section 18.3).

.DESCRIPTION
  Publishes the self-contained build (publish.ps1 -NoZip, unless -NoPublish), takes the version from the project
  (<Version>, today in Directory.Build.props), checks that dist\Dictum is that version, and compiles
  packaging\Dictum.iss with Inno Setup's command-line compiler, ISCC.exe.

  Inno Setup is a build dependency: install it per user from https://jrsoftware.org/isdl.php (the version used and
  how it was verified are in docs\DECISIONS.md). The installer is not code-signed yet, so Windows SmartScreen warns
  the first time it runs (README, "Install").

.PARAMETER NoPublish
  Package dist\Dictum as it is; it must be the project's version.
.PARAMETER Iscc
  Path to ISCC.exe. Default: the Inno Setup 7 (else 6) install recorded in the registry, per user or machine-wide,
  else ISCC.exe on PATH.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoPublish,
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$published = Join-Path $dist "Dictum"
$script = Join-Path $root "packaging\Dictum.iss"
. (Join-Path $PSScriptRoot "build-common.ps1")
$dotnet = Use-DotnetSdk

function Find-Iscc {
    foreach ($name in @("Inno Setup 7_is1", "Inno Setup 6_is1")) {
        foreach ($hive in @("HKCU:", "HKLM:")) {
            foreach ($view in @("Software", "Software\WOW6432Node")) {
                $key = "$hive\$view\Microsoft\Windows\CurrentVersion\Uninstall\$name"
                $location = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
                if ($location -and (Test-Path -LiteralPath (Join-Path $location "ISCC.exe"))) { return (Join-Path $location "ISCC.exe") }
            }
        }
    }
    return (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}

if (-not $Iscc) { $Iscc = Find-Iscc }
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) {
    throw "Inno Setup's ISCC.exe was not found. Install Inno Setup from https://jrsoftware.org/isdl.php (per user is enough), or pass -Iscc <path>."
}

# The build and its version.
$appVersion = Get-ProjectVersion $dotnet $root ""
if (-not $NoPublish) { & (Join-Path $PSScriptRoot "publish.ps1") -NoZip }
$builtVersion = Get-ExeVersion (Join-Path $published "Dictum.exe")
if ($builtVersion -ne $appVersion) { throw "dist\Dictum holds version $builtVersion but the project is $($appVersion): run without -NoPublish." }
$parts = @($appVersion.Split('.'))
if ($parts.Count -gt 4 -or ($parts | Where-Object { $_ -notmatch '^\d{1,5}$' })) { throw "Version '$appVersion' is not two to four numbers." }
while ($parts.Count -lt 4) { $parts += "0" }
$numericVersion = $parts -join '.'

# The Visual C++ runtime (docs\DECISIONS.md, "Visual C++ runtime"): a publish.ps1 -VcRuntimeDir build carries it beside
# every native runtime folder; any other build gets Setup's check for the Microsoft Visual C++ Redistributable.
$defines = @("/DAppVersion=$appVersion", "/DNumericVersion=$numericVersion", "/DPublishDir=$published", "/DOutputDir=$dist")
$nativeFolders = @(Get-NativeRuntimeFolders $published)
$withoutVc = @($nativeFolders | Where-Object { $folder = $_; @($VcRuntimeDlls | Where-Object { -not (Test-Path -LiteralPath (Join-Path $folder $_)) }).Count -gt 0 })
if ($nativeFolders.Count -gt 0 -and $withoutVc.Count -eq 0) {
    Write-Host "dist\Dictum carries the Visual C++ runtime beside all $($nativeFolders.Count) native runtime folders."
} else {
    $defines += @("/DCheckVcRedist", "/DMinVcMajor=$($MinVcRuntime.Major)", "/DMinVcMinor=$($MinVcRuntime.Minor)")
    Write-Host "dist\Dictum carries no Visual C++ runtime: Setup checks for the Microsoft Visual C++ Redistributable (x64) $MinVcRuntime or later and offers Microsoft's download."
}

# Compile. Windows PowerShell 5.1 turns a native program's stderr lines (ISCC's warnings) into errors under 2>&1,
# which "Stop" would make fatal: relax it for the call and check the exit code.
Write-Host "Compiling $script with $Iscc..."
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $lines = @(& $Iscc @defines $script 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
$lines | Where-Object { $code -ne 0 -or $_ -match 'warning|error|Successful|Compiler engine version' } | ForEach-Object { Write-Host "    $($_.Trim())" }
if ($code -ne 0) { throw "ISCC failed with exit code $code." }

$setup = Join-Path $dist "Dictum-Setup-$appVersion.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "ISCC reported success but $setup is missing." }
$size = (Get-Item -LiteralPath $setup).Length
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Write-Host ("Built {0}: {1:N0} bytes ({2:0.0} MiB), SHA-256 {3}, version {4}, from {5}." -f $setup, $size, ($size / 1MB), $hash, $appVersion, $published)
Write-Host "  Not code-signed: SmartScreen shows 'Windows protected your PC' on first run (More info > Run anyway)."
