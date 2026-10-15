<#
.SYNOPSIS
  Publishes Dictum to dist\Dictum\ (self-contained win-x64) and zips it to dist\Dictum-<version>-win-x64.zip.

.DESCRIPTION
  Self-contained, so the target needs no .NET install (staff machines, Azure VMs). The folder carries all three
  whisper.cpp native runtimes (Vulkan, CPU, CPU without AVX); the script fails if any is missing.
  -FrameworkDependent gives a much smaller folder that needs the .NET 10 Runtime installed (no Desktop Runtime: the
  windows are Avalonia, not WPF or WinForms).
  -Version overrides <Version> (Directory.Build.props) for this build only, e.g. a Store test package before the
  repository's version is raised (package-msix.ps1 passes it through).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$FrameworkDependent,
    [switch]$NoZip,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$out = Join-Path $dist "Dictum"

. (Join-Path $PSScriptRoot "build-common.ps1")
$dotnet = Use-DotnetSdk
if ($Version -and $Version -notmatch '^\d+(\.\d+){1,3}$') { throw "-Version must be two to four numbers, such as 1.0.0 (got '$Version')." }

if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
$publishArgs = @("publish", (Join-Path $root "src\Dictum.Windows\Dictum.Windows.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", $selfContained, "-o", $out, "-nologo")
if ($Version) { $publishArgs += "-p:Version=$Version" }
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Whisper.net's and LLamaSharp's build targets can copy other platforms' native files; this app is Windows x64 only.
$extras = @("runtimes\vulkan\linux-x64", "runtimes\noavx\linux-x64", "runtimes\noavx\win-x86", "runtimes\win-arm64", "runtimes\win-x86",
            "runtimes\linux-x64", "runtimes\linux-arm64", "runtimes\linux-musl-x64", "runtimes\osx-x64", "runtimes\osx-arm64")
foreach ($extra in $extras) {
    $path = Join-Path $out $extra
    if (Test-Path $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

$native = @("whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll")
# Batch 2 data files ship as plain text beside Dictum.exe: the profanity word list and the calibration sentences. The
# Avalonia UI needs its Skia and HarfBuzz native libraries.
$required = @("Dictum.exe", "profanity.txt", "calibration-sentences.txt", "libSkiaSharp.dll", "libHarfBuzzSharp.dll") +
    ($native | ForEach-Object { "runtimes\win-x64\$_" }) +
    ($native | ForEach-Object { "runtimes\noavx\win-x64\$_" }) +
    (($native + "ggml-vulkan-whisper.dll") | ForEach-Object { "runtimes\vulkan\win-x64\$_" }) +
    # llama.cpp for the local cleanup model (LLamaSharp backends): the Vulkan build, plus CPU builds per AVX level.
    (@("llama.dll", "ggml.dll", "ggml-base.dll", "ggml-vulkan.dll") | ForEach-Object { "runtimes\win-x64\native\vulkan\$_" }) +
    (@("avx", "avx2", "avx512", "noavx") | ForEach-Object { "runtimes\win-x64\native\$_\llama.dll"; "runtimes\win-x64\native\$_\ggml-cpu.dll" })
$missing = $required | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "Publish output is missing: $($missing -join ', ')" }

$bytes = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
$files = @(Get-ChildItem $out -Recurse -File).Count
Write-Host ("Published to {0}: {1} files, {2:N0} bytes ({3:0.0} MiB), self-contained: {4}. Native whisper and llama runtimes present:" -f $out, $files, $bytes, ($bytes / 1MB), $selfContained)
$required | Where-Object { $_ -like "runtimes*" } | ForEach-Object { Write-Host "  $_" }
Write-Host "Data files present: profanity.txt, calibration-sentences.txt"

# SPEC section 17 (amended 16/10/2026): manifest.txt lists every shipped file (relative path, bytes, SHA-256). "Remove
# Dictum completely" deletes only the files it lists that are still there unchanged, so a copy unzipped into a folder that
# holds other files never takes those with it. Written after every other file, so it lists exactly what ships (not itself).
$manifest = Join-Path $out "manifest.txt"
$outFull = (Resolve-Path -LiteralPath $out).Path.TrimEnd('\')
$entries = @(Get-ChildItem -LiteralPath $outFull -Recurse -File | Where-Object { $_.Name -ne "manifest.txt" -or $_.DirectoryName -ne $outFull } |
    Sort-Object FullName | ForEach-Object {
        "{0}`t{1}`t{2}" -f $_.FullName.Substring($outFull.Length + 1), $_.Length, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
[IO.File]::WriteAllLines($manifest, [string[]](@("# Dictum shipped files: relative path<TAB>bytes<TAB>sha256 (read by Remove Dictum completely)") + $entries))
Write-Host ("Manifest: {0} files listed in {1}" -f $entries.Count, $manifest)

if (-not $NoZip) {
    $builtVersion = Get-ExeVersion (Join-Path $out "Dictum.exe")
    $zip = Join-Path $dist "Dictum-$builtVersion-win-x64.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("Zipped to {0}: {1:N0} bytes ({2:0.0} MiB)" -f $zip, (Get-Item $zip).Length, ((Get-Item $zip).Length / 1MB))
}
