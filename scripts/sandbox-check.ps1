<#
.SYNOPSIS
  Runs Dictum's --selftest inside Windows Sandbox, a clean Windows without the Visual C++ runtime, and passes only if the
  speech engine loads (and the clean-up engine, when -CleanupModel is given).

.DESCRIPTION
  Store certification, 16/10/2026 (docs\DECISIONS.md, "Visual C++ runtime"): on a clean Windows neither whisper.cpp nor
  llama.cpp loaded, because both import the Visual C++ runtime, which a development PC nearly always has. Windows Sandbox
  is a clean Windows that is discarded when it closes, so it shows the failure a tester's PC shows.

  Each -Published folder (a publish.ps1 output, or an unzipped release) runs `Dictum.exe --selftest` once per -Gpu value.
  -Package registers an MSIX layout (packaging\out\msix-<flavour>\layout, with the package's own identity) in the
  sandbox's Developer Mode, after installing -Framework, Microsoft's signed Microsoft.VCLibs.x64.14.00.Desktop.appx
  (the framework the Store would install with Dictum; docs\TESTING-MSIX.md says where to get it), and runs the
  self-test through the package's Dictum.exe alias. Without -Framework the registration fails on the missing
  dependency, as a sideload does on a PC without the framework.

  Needs the Windows Sandbox feature, and no other sandbox open (one runs at a time). Its window opens on the desktop for
  a few minutes and closes itself. The sandbox sees the folders it is given read-only and writes only to -Out;
  networking, the microphone, the camera, the clipboard and printers are off, so the microphone line of every self-test
  fails and does not count. The virtual GPU is turned off too (VGpu Disable), but that does NOT make the sandbox a PC
  without a GPU: it keeps a Vulkan loader in System32, and Vulkan has found a device there (an Intel device, observed by
  the investigation of 21/10/2026; Vulkan [OK] runs in docs\DECISIONS.md, 17/10/2026), so the whisper runtime loads its
  Vulkan build. That is why these runs missed the launch crash of 21/10/2026 on PCs where the Vulkan runtime cannot load.
  -NoVulkanLoader takes the loader away (below) to show that case.

.PARAMETER Published
  Folders holding Dictum.exe, comma-separated. Default: dist\Dictum, unless -Package is given.
.PARAMETER SpeechModel
  A catalogue ggml .bin (read only). Default: ggml-base.en.bin in %LOCALAPPDATA%\Dictum\models.
.PARAMETER CleanupModel
  A catalogue .gguf (read only); its engine must load too.
.PARAMETER Gpu
  whisper.gpu and llm.gpu for each run, comma-separated: auto tries Vulkan first when the Vulkan loader (vulkan-1.dll)
  loads, vulkan tries Vulkan first always, cpu loads the CPU runtimes. Default: auto,cpu.
.PARAMETER NoVulkanLoader
  Inside the sandbox, before any run, renames System32\vulkan-1*.dll (taking ownership first: they belong to
  TrustedInstaller), so it is a PC without a Vulkan loader, as on the PCs of the launch crash of 21/10/2026. Only the
  disposable sandbox's own System32 is changed; environment.txt records what was renamed. Expect auto to load the CPU
  runtime ([OK]), not abort. Use a layout that loads in a clean Windows at all (a publish.ps1 -VcRuntimeDir build, or
  -Package with -Framework): without the Visual C++ runtime no whisper runtime loads, so no Vulkan load fails either.
.PARAMETER Out
  Results folder. Default: packaging\out\sandbox\<date-time>.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\sandbox-check.ps1 -Published dist\Dictum -CleanupModel "$env:LOCALAPPDATA\Dictum\models\qwen2.5-0.5b-instruct-q4_k_m.gguf"
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\sandbox-check.ps1 -Published dist\Dictum -Gpu auto,cpu -NoVulkanLoader
#>
[CmdletBinding()]
param(
    [string[]]$Published = @(),
    [string]$SpeechModel = (Join-Path $env:LOCALAPPDATA "Dictum\models\ggml-base.en.bin"),
    [string]$CleanupModel,
    [string[]]$Gpu = @("auto", "cpu"),
    [string]$Package,
    [string]$Framework,
    [string]$Out,
    [switch]$NoVulkanLoader,
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# powershell -File passes "a,b" as one string: split the lists here.
$Published = @($Published | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Gpu = @($Gpu | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$badGpu = @($Gpu | Where-Object { $_ -notin "auto", "vulkan", "cpu" })
if ($badGpu -or -not $Gpu) { throw "-Gpu takes auto, vulkan and cpu (got '$($Gpu -join ',')')." }
$sandbox = Join-Path $env:SystemRoot "System32\WindowsSandbox.exe"
if (-not (Test-Path -LiteralPath $sandbox)) { throw "Windows Sandbox is not installed: turn on the Windows feature ""Windows Sandbox"" first." }
if (Get-Process -Name "WindowsSandbox*" -ErrorAction SilentlyContinue) { throw "A Windows Sandbox is already open; close it first (only one runs at a time)." }
if (-not $Published -and -not $Package) { $Published = @(Join-Path $root "dist\Dictum") }
if (-not $Out) { $Out = Join-Path $root ("packaging\out\sandbox\" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
if (Test-Path -LiteralPath (Join-Path $Out "done.txt")) { throw "$Out already holds a finished check: choose a new -Out." }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

# Mapped folders: name in the sandbox -> host folder, all read-only except the output.
$maps = [ordered]@{ "C:\sbx\scripts" = $PSScriptRoot }
function Add-Map([string]$HostPath, [string]$Name, [switch]$File) {
    if (-not (Test-Path -LiteralPath $HostPath)) { throw "$HostPath does not exist." }
    $folder = if ($File) { Split-Path -Parent (Resolve-Path -LiteralPath $HostPath).Path } else { (Resolve-Path -LiteralPath $HostPath).Path }
    $existing = @($maps.Keys | Where-Object { $maps[$_] -eq $folder })
    $inside = if ($existing) { $existing[0] } else { "C:\sbx\$Name" }
    $maps[$inside] = $folder
    if ($File) { return Join-Path $inside (Split-Path -Leaf $HostPath) } else { return $inside }
}

$runs = @()
$index = 0
foreach ($folder in $Published) {
    if (-not (Test-Path -LiteralPath (Join-Path $folder "Dictum.exe"))) { throw "$folder holds no Dictum.exe." }
    $index++
    $label = "$index-" + (Split-Path -Leaf (Resolve-Path -LiteralPath $folder).Path)
    $inside = Add-Map $folder "published-$index"
    foreach ($g in $Gpu) { $runs += [ordered]@{ name = "$label-$g"; folder = $inside; gpu = $g; packaged = $false } }
}
$job = [ordered]@{ speechModel = (Add-Map $SpeechModel "models-speech" -File); cleanupModel = $null; package = $null; framework = $null
    noVulkanLoader = [bool]$NoVulkanLoader }
if ($CleanupModel) { $job.cleanupModel = Add-Map $CleanupModel "models-cleanup" -File }
if ($Package) {
    if (-not (Test-Path -LiteralPath (Join-Path $Package "AppxManifest.xml"))) { throw "$Package holds no AppxManifest.xml (an MSIX layout such as packaging\out\msix-store\layout)." }
    $job.package = Add-Map $Package "package"
    if ($Framework) {
        if (-not (Test-Path -LiteralPath $Framework -PathType Leaf)) { throw "-Framework must be the framework's signed .appx file (Windows registers a framework from no folder)." }
        $job.framework = Add-Map $Framework "framework" -File
    }
    foreach ($g in $Gpu) { $runs += [ordered]@{ name = "package-$g"; folder = $null; gpu = $g; packaged = $true } }
}
$job.runs = $runs
[IO.File]::WriteAllText((Join-Path $Out "job.json"), ($job | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))

$mapped = foreach ($inside in $maps.Keys) {
    "    <MappedFolder><HostFolder>{0}</HostFolder><SandboxFolder>{1}</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" -f
        [Security.SecurityElement]::Escape($maps[$inside]), $inside
}
$mapped += "    <MappedFolder><HostFolder>{0}</HostFolder><SandboxFolder>C:\sbx\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" -f
    [Security.SecurityElement]::Escape($Out)
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "sandbox-check.wsb")) -replace '(?s)^\s*<!--.*?-->\s*', ''
$wsb = Join-Path $Out "sandbox-check.wsb"
[IO.File]::WriteAllText($wsb, $template.Replace("{{MappedFolders}}", ($mapped -join "`r`n")), (New-Object System.Text.UTF8Encoding $false))

Write-Host "Starting Windows Sandbox ($($runs.Count) self-test runs; results in $Out)..."
$maps.GetEnumerator() | ForEach-Object { Write-Host ("  {0} <- {1} (read-only)" -f $_.Key, $_.Value) }
Start-Process -FilePath $sandbox -ArgumentList "`"$wsb`""
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path -LiteralPath (Join-Path $Out "done.txt"))) {
    if ((Get-Date) -gt $deadline) { throw "No result after $TimeoutMinutes minutes; see $Out\progress.txt (the sandbox may still be open)." }
    Start-Sleep -Seconds 5
}

# The verdict: the engine lines of every run.
Write-Host ""
Get-Content -LiteralPath (Join-Path $Out "environment.txt") | ForEach-Object { Write-Host "  $_" }
if (Test-Path -LiteralPath (Join-Path $Out "package.txt")) { Get-Content -LiteralPath (Join-Path $Out "package.txt") | ForEach-Object { Write-Host "  $_" } }
# Each run's exit code: -1073740791 (0xC0000409) is the abort of the launch crash of 21/10/2026.
Get-Content -LiteralPath (Join-Path $Out "progress.txt") | Where-Object { $_ -match 'self-test .*: (exit|not run)' } | ForEach-Object { Write-Host "  $_" }
$failed = 0
foreach ($run in $runs) {
    $file = Join-Path $Out "selftest-$($run.name).txt"
    $lines = if (Test-Path -LiteralPath $file) { @(Get-Content -LiteralPath $file) } else { @() }
    $wanted = @("whisper runtime") + @(if ($CleanupModel) { "cleanup backend" })
    Write-Host "$($run.name):"
    foreach ($label in $wanted) {
        $line = $lines | Where-Object { $_ -like "$label *" } | Select-Object -First 1
        if (-not $line) { $line = "$label  (no such line: see $file)" }
        if ($line -notmatch '\[OK\]\s*$') { $failed++ }
        Write-Host "  $line"
    }
}
if ($failed) { Write-Host "FAIL: $failed engine line(s) did not load. Full output: $Out"; exit 1 }
Write-Host "PASS: every engine loaded in a clean Windows. Full output: $Out"
