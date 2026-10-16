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

  The Visual C++ runtime (docs\DECISIONS.md, "Visual C++ runtime"): every whisper.cpp and llama.cpp DLL imports
  msvcp140.dll, vcruntime140.dll and vcruntime140_1.dll, and the CPU builds vcomp140.dll, which a clean Windows does
  not have. -VcRuntimeDir copies those four DLLs beside every native runtime folder (LLamaSharp does not look beside
  Dictum.exe), so the zip and the installer run without the Microsoft Visual C++ Redistributable. Microsoft allows
  only licensed Visual Studio users to redistribute them, so the source must be the redistributable folder of a
  Visual Studio or Build Tools install the publisher is licensed for. Without -VcRuntimeDir the build carries none and
  needs the redistributable on the PC that runs it (README, "Install"). package-msix.ps1 never ships them: the Store
  package declares Microsoft's framework package instead.

  The bundled speech model (Decision 16, 17/10/2026): every layout built from dist\Dictum (the zip, the installer and
  both MSIX flavours) carries models\ggml-base.en.bin beside Dictum.exe, so first use and Store testing need no
  download, and THIRD-PARTY-NOTICES.txt for its licence. download-model.ps1 checks it against the pinned commit, size
  and SHA-256 it shares with the app's catalogue. A matching copy in %LOCALAPPDATA%\Dictum\models is reused (read
  only); otherwise it is downloaded from huggingface.co, and dist\model-cache keeps it for the next publish.

.PARAMETER VcRuntimeDir
  A Visual Studio or Build Tools redistributable folder, VC\Redist\MSVC\<version>\x64 (it holds Microsoft.VC14x.CRT
  and Microsoft.VC14x.OpenMP), or a folder holding the four DLLs. Each must carry a valid Microsoft signature and be
  version 14.51 or later (the newest MSVC linker among the native DLLs; scripts\build-common.ps1).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -VcRuntimeDir "C:\Program Files\Microsoft Visual Studio\<version>\BuildTools\VC\Redist\MSVC\<version>\x64"
#>
param(
    [string]$Configuration = "Release",
    [switch]$FrameworkDependent,
    [switch]$NoZip,
    [string]$Version,
    [string]$VcRuntimeDir
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$out = Join-Path $dist "Dictum"

. (Join-Path $PSScriptRoot "build-common.ps1")
$dotnet = Use-DotnetSdk
if ($Version -and $Version -notmatch '^\d+(\.\d+){1,3}$') { throw "-Version must be two to four numbers, such as 1.0.0 (got '$Version')." }

# The Visual C++ runtime to copy in, checked before the slow publish: each DLL once, Microsoft-signed, new enough.
$vcRuntime = @()
if ($VcRuntimeDir) {
    if (-not (Test-Path -LiteralPath $VcRuntimeDir -PathType Container)) { throw "-VcRuntimeDir $VcRuntimeDir is not a folder." }
    $vcRuntime = foreach ($name in $VcRuntimeDlls) {
        $candidates = @(Join-Path $VcRuntimeDir $name) +
            @(Get-ChildItem -LiteralPath $VcRuntimeDir -Directory -Filter "Microsoft.VC*" | ForEach-Object { Join-Path $_.FullName $name })
        $found = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($found.Count -ne 1) { throw "-VcRuntimeDir: expected one $name in $VcRuntimeDir or its Microsoft.VC* folders, found $($found.Count)." }
        $file = Get-Item -LiteralPath $found[0]
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
            throw "$($file.FullName) is not signed by Microsoft ($($signature.Status), $($signature.SignerCertificate.Subject))."
        }
        $info = $file.VersionInfo
        if ([version]"$($info.FileMajorPart).$($info.FileMinorPart)" -lt $MinVcRuntime) {
            throw "$($file.FullName) is version $($info.FileVersion); the native DLLs need $MinVcRuntime or later (a newer Visual Studio or Build Tools)."
        }
        $file
    }
}

# The bundled speech model, fetched or reused before the slow publish: download-model.ps1 verifies the size and SHA-256
# (a copy already in the cache, or reused from %LOCALAPPDATA%\Dictum\models, included) and throws on a mismatch.
$bundledModel = "base.en"
$modelFile = "ggml-$bundledModel.bin"
$modelCache = Join-Path $dist "model-cache"
$cachedModel = Join-Path $modelCache $modelFile
$userModel = Join-Path $env:LOCALAPPDATA "Dictum\models\$modelFile"
if (-not (Test-Path -LiteralPath $cachedModel) -and (Test-Path -LiteralPath $userModel)) {
    New-Item -ItemType Directory -Force -Path $modelCache | Out-Null
    Copy-Item -LiteralPath $userModel -Destination $cachedModel
    Write-Host "Reusing $userModel (copied to $modelCache; download-model.ps1 checks it next)."
}
& (Join-Path $PSScriptRoot "download-model.ps1") -Model $bundledModel -ModelsDir $modelCache

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

# The bundled speech model and its notice (checked above).
New-Item -ItemType Directory -Force -Path (Join-Path $out "models") | Out-Null
Copy-Item -LiteralPath $cachedModel -Destination (Join-Path $out "models\$modelFile")
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.txt") -Destination $out

$native = @("whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll")
# Batch 2 data files ship as plain text beside Dictum.exe: the profanity word list and the calibration sentences. The
# Avalonia UI needs its Skia and HarfBuzz native libraries.
$required = @("Dictum.exe", "profanity.txt", "calibration-sentences.txt", "libSkiaSharp.dll", "libHarfBuzzSharp.dll",
              "models\$modelFile", "THIRD-PARTY-NOTICES.txt") +
    ($native | ForEach-Object { "runtimes\win-x64\$_" }) +
    ($native | ForEach-Object { "runtimes\noavx\win-x64\$_" }) +
    (($native + "ggml-vulkan-whisper.dll") | ForEach-Object { "runtimes\vulkan\win-x64\$_" }) +
    # llama.cpp for the local cleanup model (LLamaSharp backends): the Vulkan build, plus CPU builds per AVX level.
    (@("llama.dll", "ggml.dll", "ggml-base.dll", "ggml-vulkan.dll") | ForEach-Object { "runtimes\win-x64\native\vulkan\$_" }) +
    (@("avx", "avx2", "avx512", "noavx") | ForEach-Object { "runtimes\win-x64\native\$_\llama.dll"; "runtimes\win-x64\native\$_\ggml-cpu.dll" })
$missing = $required | Where-Object { -not (Test-Path (Join-Path $out $_)) }
if ($missing) { throw "Publish output is missing: $($missing -join ', ')" }

# The Visual C++ runtime beside every native runtime folder (Get-NativeRuntimeFolders, build-common.ps1).
$nativeFolders = @(Get-NativeRuntimeFolders $out)
foreach ($folder in $nativeFolders) { foreach ($file in $vcRuntime) { Copy-Item -LiteralPath $file.FullName -Destination $folder -Force } }

$bytes = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
$files = @(Get-ChildItem $out -Recurse -File).Count
Write-Host ("Published to {0}: {1} files, {2:N0} bytes ({3:0.0} MiB), self-contained: {4}. Native whisper and llama runtimes present:" -f $out, $files, $bytes, ($bytes / 1MB), $selfContained)
$required | Where-Object { $_ -like "runtimes*" } | ForEach-Object { Write-Host "  $_" }
Write-Host "Data files present: profanity.txt, calibration-sentences.txt"
Write-Host ("Bundled speech model: models\{0}, {1:N0} bytes (size and SHA-256 checked by download-model.ps1), with THIRD-PARTY-NOTICES.txt" -f
    $modelFile, (Get-Item -LiteralPath (Join-Path $out "models\$modelFile")).Length)
if ($vcRuntime) {
    Write-Host ("Visual C++ runtime {0} ({1}) copied beside {2} native runtime folders, from {3}" -f
        (($vcRuntime | ForEach-Object { $_.VersionInfo.FileVersion } | Sort-Object -Unique) -join ', '),
        ($vcRuntime.Name -join ', '), $nativeFolders.Count, $VcRuntimeDir)
} else {
    Write-Warning ("No -VcRuntimeDir: this build carries no Visual C++ runtime, so on a PC without the Microsoft Visual C++ " +
        "Redistributable (x64) $MinVcRuntime or later, speech recognition and AI clean-up cannot load (README, Install).")
}

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
