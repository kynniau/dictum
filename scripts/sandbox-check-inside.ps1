# Runs inside Windows Sandbox for scripts\sandbox-check.ps1: reads C:\sbx\out\job.json, writes every result beside it,
# then shuts the sandbox down. Never run it anywhere else: it turns on Developer Mode and registers packages, which is
# harmless only in a sandbox that is discarded when it closes.
$ErrorActionPreference = "Continue"
$out = "C:\sbx\out"
function Note([string]$Line) { "$(Get-Date -Format HH:mm:ss) $Line" | Add-Content -LiteralPath "$out\progress.txt" -Encoding UTF8 }
function Write-Utf8([string]$Path, [string[]]$Lines) { [IO.File]::WriteAllLines($Path, $Lines, (New-Object System.Text.UTF8Encoding $false)) }

function Invoke-SelfTest($Run, $Job) {
    # A settings.json per run: the models from the host (read-only), the run's GPU choice, and F24 as the hotkey so the
    # self-test's 5-second key check cannot catch a real key.
    $config = "C:\sbx\config\$($Run.name)"
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    $settings = [ordered]@{ hotkey = "F24"; whisper = [ordered]@{ model = $Job.speechModel; gpu = $Run.gpu } }
    if ($Job.cleanupModel) { $settings.llm = [ordered]@{ provider = "local"; model = $Job.cleanupModel; gpu = $Run.gpu } }
    Write-Utf8 "$config\settings.json" @($settings | ConvertTo-Json -Depth 4)
    $exe = if ($Run.packaged) { Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\Dictum.exe" } else { Join-Path $Run.folder "Dictum.exe" }
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Utf8 "$out\selftest-$($Run.name).txt" @("not run: $exe does not exist")
        Note "self-test $($Run.name): not run, no $exe"
        return
    }
    $started = Get-Date
    $lines = @(& $exe --selftest --config $config 2>&1 | ForEach-Object { "$_" })
    Write-Utf8 "$out\selftest-$($Run.name).txt" $lines
    Note ("self-test {0}: exit {1} after {2:0} s" -f $Run.name, $LASTEXITCODE, ((Get-Date) - $started).TotalSeconds)
}

try {
    Note "started"
    $job = Get-Content -LiteralPath "$out\job.json" -Raw | ConvertFrom-Json

    # What this clean Windows has: the reason for the check.
    $vc = foreach ($name in "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll") {
        "System32\{0} present: {1}" -f $name, (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$name"))
    }
    $vcLibs = @(Get-AppxPackage -AllUsers -Name "Microsoft.VCLibs*" | ForEach-Object { "$($_.Name) $($_.Version)" })

    # The Vulkan loader. VGpu Disable does not remove it (and Vulkan has still found a device here), so -NoVulkanLoader
    # renames it in this disposable System32: the PC of the launch crash of 21/10/2026, where the Vulkan runtime cannot load.
    $system32 = Join-Path $env:SystemRoot "System32"
    $loaders = @(Get-ChildItem -LiteralPath $system32 -Filter "vulkan-1*.dll" -File -ErrorAction SilentlyContinue)
    $vulkan = @("Vulkan loader in System32: $(if ($loaders) { ($loaders.Name) -join ', ' } else { 'none' })")
    if ($job.noVulkanLoader) {
        foreach ($loader in $loaders) {
            & takeown.exe /f $loader.FullName /a | Out-Null
            & icacls.exe $loader.FullName /grant "*S-1-5-32-544:F" | Out-Null # the Administrators group, by SID (any language)
            try {
                Rename-Item -LiteralPath $loader.FullName -NewName "$($loader.Name).disabled" -ErrorAction Stop
                $vulkan += "-NoVulkanLoader: renamed $($loader.Name) to $($loader.Name).disabled"
            } catch {
                $vulkan += "-NoVulkanLoader: could not rename $($loader.Name): $($_.Exception.Message)"
            }
        }
        $left = @(Get-ChildItem -LiteralPath $system32 -Filter "vulkan-1*.dll" -File -ErrorAction SilentlyContinue)
        $vulkan += "Vulkan loader in System32 for the runs: $(if ($left) { ($left.Name) -join ', ' } else { 'none' })"
    }
    Write-Utf8 "$out\environment.txt" (@("OS: $([Environment]::OSVersion.VersionString)") + $vc +
        @("Visual C++ Redistributable registry key present: $(Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')",
          "VCLibs packages before the check: $(if ($vcLibs) { $vcLibs -join ', ' } else { 'none' })") + $vulkan)

    # Unpackaged builds first, before any package is registered.
    foreach ($run in @($job.runs | Where-Object { -not $_.packaged })) { Invoke-SelfTest $run $job }

    if ($job.package) {
        # The framework first, from Microsoft's signed .appx, as the Store would install it: Windows registers a framework
        # package from no loose folder (observed 17/10/2026: in Developer Mode "DevelopmentMode is not allowed when
        # registering framework or resource packages"; without it "the manifest is not in the package root"). Then
        # Dictum's own layout, unsigned, in Developer Mode.
        & reg.exe add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /t REG_DWORD /f /v AllowDevelopmentWithoutDevLicense /d 1 | Out-Null
        $registration = @()
        if ($job.framework) {
            try {
                Add-AppxPackage -Path $job.framework -ErrorAction Stop
                $registration += "installed the framework from $($job.framework)"
            } catch {
                $registration += "installing the framework from $($job.framework) failed: $($_.Exception.Message)"
            }
        }
        try {
            Add-AppxPackage -Register (Join-Path $job.package "AppxManifest.xml") -ErrorAction Stop
            $registration += "registered $($job.package)"
        } catch {
            $registration += "registering $($job.package) failed: $($_.Exception.Message)"
        }
        $registered = @(Get-AppxPackage | Where-Object { $_.Name -in "Kynni.Dictum", "Microsoft.VCLibs.140.00.UWPDesktop" } |
            ForEach-Object { "installed: $($_.Name) $($_.Version) ($($_.PackageFamilyName))" })
        Write-Utf8 "$out\package.txt" (@($registration) + $registered)
        Note "packages: $(@($registration) -join '; ')"
        foreach ($run in @($job.runs | Where-Object { $_.packaged })) { Invoke-SelfTest $run $job }
    }
} catch {
    Note "error: $($_.Exception.Message)"
} finally {
    Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA "Dictum\logs") -Destination "$out\logs" -Recurse -ErrorAction SilentlyContinue
    Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Packages\Kynni.Dictum_*\LocalCache\Local\Dictum\logs") -Directory -ErrorAction SilentlyContinue |
        Copy-Item -Destination "$out\package-logs" -Recurse -ErrorAction SilentlyContinue
    Note "done"
    Write-Utf8 "$out\done.txt" @("done")
    & shutdown.exe /s /t 5
}
