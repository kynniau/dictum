# Helpers shared by the build scripts (publish.ps1, package-msix.ps1, build-installer.ps1). Dot-source it.
# install.ps1 and uninstall.ps1 do not use it: they ship beside a published build and must run on their own.

# The Visual C++ runtime DLLs that whisper.cpp and llama.cpp import (docs\DECISIONS.md, "Visual C++ runtime"), and the
# oldest runtime they may run on: Microsoft requires a runtime at least as recent as the MSVC build tools that built the
# app ("Microsoft Visual C++ Redistributable latest supported downloads", read 17/10/2026), and the newest linker among
# the native DLLs is 14.51 (Whisper.net.Runtime.NoAvx 1.9.1; the others 14.44). tests\Dictum.Tests\VcRuntimeTests.cs
# checks both against the DLLs' PE headers. The Store package takes the runtime from Microsoft's framework package
# instead (packaging\AppxManifest.template.xml).
$VcRuntimeDlls = @("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll")
$MinVcRuntime = [version]"14.51"

function Get-NativeRuntimeFolders([string]$Published) {
    # Every folder under runtimes\ that holds a DLL: the whisper.cpp and llama.cpp runtimes (8 in the 1.0.0 build). In a
    # direct-download build each needs the Visual C++ runtime beside it: each engine loads its DLLs by full path, Windows
    # looks for their imports in the DLL's own folder first, and LLamaSharp does not look beside Dictum.exe.
    $runtimes = Join-Path $Published "runtimes"
    if (-not (Test-Path -LiteralPath $runtimes)) { return }
    Get-ChildItem -LiteralPath $runtimes -Recurse -File -Filter *.dll | ForEach-Object DirectoryName | Sort-Object -Unique
}

function Use-DotnetSdk {
    # The first dotnet that actually has a 10.x SDK: a machine-wide install may carry runtimes only.
    $candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")) |
        Where-Object { $_ -and (Test-Path $_) }
    $dotnet = $candidates | Where-Object { (& $_ --list-sdks 2>$null) -match '^10\.' } | Select-Object -First 1
    if (-not $dotnet) { throw "No .NET 10 SDK found on PATH or in %LOCALAPPDATA%\Microsoft\dotnet. Install it first (see README)." }
    $env:DOTNET_ROOT = Split-Path $dotnet
    # Avalonia's build tasks send anonymous usage statistics unless told not to (Avalonia.BuildServices);
    # Directory.Build.targets also replaces the target that sends them, so a build from an IDE stays quiet too.
    $env:AVALONIA_TELEMETRY_OPTOUT = "1"
    # The .NET CLI's own usage telemetry is on by default; the build scripts opt out of it as well.
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_NOLOGO = "1"
    return $dotnet
}

function Get-ProjectVersion([string]$Dotnet, [string]$Root, [string]$Override) {
    # <Version> as MSBuild evaluates it for Dictum.exe (Directory.Build.props today), or the override passed to publish.
    $project = Join-Path $Root "src\Dictum.Windows\Dictum.Windows.csproj"
    $arguments = @("msbuild", $project, "-getProperty:Version", "-nologo")
    if ($Override) { $arguments += "-p:Version=$Override" }
    $version = (& $Dotnet @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $version) { throw "Could not read <Version> from $project (exit $LASTEXITCODE)." }
    return $version
}

function Get-ExeVersion([string]$Exe) {
    # The product version Windows shows for the built exe, without the "+<commit>" build metadata.
    if (-not (Test-Path -LiteralPath $Exe)) { throw "$Exe not found: publish first (scripts\publish.ps1)." }
    return (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion -replace '\+.*$', ''
}
