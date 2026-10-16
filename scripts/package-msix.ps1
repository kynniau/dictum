<#
.SYNOPSIS
  Builds Dictum's MSIX package (SPEC section 18.1): -Test for sideloading tests, -Store for Partner Center.

.DESCRIPTION
  -Test   dist\Dictum-<version>-test.msix. Identity Kynni.Dictum with the self-signed test certificate's subject as
          Publisher, signed with that certificate: run scripts\make-test-cert.ps1 once first. Installs only where a
          tester has trusted the certificate (docs\TESTING-MSIX.md).
  -Store  dist\Dictum-<version>-store.msix. The Partner Center identity (SPEC section 18.1), left UNSIGNED: the
          Microsoft Store signs packages itself when it publishes them ("App package requirements for MSIX app",
          Microsoft Learn, read 16/10/2026). Refuses a version the Store rejects: the first part 0 or the fourth not 0.

  Steps: publish self-contained win-x64 (publish.ps1 -NoZip, unless -NoPublish); restore packaging\tools, which
  downloads Svg.Skia and Microsoft.Windows.SDK.BuildTools into packaging\out\packages (no machine-wide Windows SDK);
  render every logo from site\dictum.svg (site\favicon.svg, the same mark with a faint light outline, for dark
  surfaces); write packaging\AppxManifest.xml from packaging\AppxManifest.template.xml; index the logos with MakePri;
  pack with MakeAppx; sign with SignTool (-Test only). Work files go to packaging\out\msix-<test|store>.

.PARAMETER NoPublish
  Package dist\Dictum as it is; it must be the project's version.
.PARAMETER Version
  Build at this version instead of <Version> in Directory.Build.props (passed to publish.ps1, so Dictum.exe and the
  package agree).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\package-msix.ps1 -Test
#>
[CmdletBinding(DefaultParameterSetName = "Test")]
param(
    [Parameter(ParameterSetName = "Test", Mandatory)] [switch]$Test,
    [Parameter(ParameterSetName = "Store", Mandatory)] [switch]$Store,
    [switch]$NoPublish,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "build-common.ps1")
$dotnet = Use-DotnetSdk

# The Partner Center product identity (SPEC section 18.1). The test build keeps the Name and display names and takes
# its Publisher from the test certificate, so the two builds have different package families.
$identityName = "Kynni.Dictum"
$storePublisher = "CN=CDA37BA0-481B-4F79-9D16-A8AD5ABB1D76"
$displayName = "Dictum"
$publisherDisplayName = "Kynni"

$flavour = if ($Store) { "store" } else { "test" }
$dist = Join-Path $root "dist"
$published = Join-Path $dist "Dictum"
$work = Join-Path $root "packaging\out\msix-$flavour"
$toolsProject = Join-Path $root "packaging\tools\PackagingTools.csproj"
$template = Join-Path $root "packaging\AppxManifest.template.xml"
$manifestPath = Join-Path $root "packaging\AppxManifest.xml"

function Invoke-Tool([string]$Exe, [string[]]$Arguments, [string]$Keep, [switch]$Utf16) {
    # Windows PowerShell 5.1 turns a native program's stderr lines into errors under 2>&1, which "Stop" would make
    # fatal on the first warning: relax the preference for the call and check the exit code instead. With -Keep only
    # matching lines are printed (MakeAppx names every file it packs), and everything is printed if the tool fails.
    # MakePri writes UTF-16 (-Utf16), which PowerShell decodes with the console's encoding.
    $previousPreference = $ErrorActionPreference
    $previousEncoding = [Console]::OutputEncoding
    $ErrorActionPreference = "Continue"
    try {
        if ($Utf16) { [Console]::OutputEncoding = [Text.Encoding]::Unicode }
        $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Where-Object { $_.Trim() })
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
        [Console]::OutputEncoding = $previousEncoding
    }
    $shown = if ($code -ne 0 -or -not $Keep) { $lines } else { @($lines | Where-Object { $_ -match $Keep }) }
    $shown | ForEach-Object { Write-Host "    $($_.Trim())" }
    if ($shown.Count -lt $lines.Count) { Write-Host "    ($($lines.Count - $shown.Count) more lines not shown)" }
    if ($code -ne 0) { throw "$(Split-Path $Exe -Leaf) $($Arguments[0]) failed with exit code $code." }
}

# 1. The build, and its version.
if ($NoPublish -and $Version) { throw "-Version needs a publish: drop -NoPublish." }
$appVersion = Get-ProjectVersion $dotnet $root $Version
if (-not $NoPublish) {
    $publishArgs = @{ NoZip = $true }
    if ($Version) { $publishArgs.Version = $Version }
    & (Join-Path $PSScriptRoot "publish.ps1") @publishArgs
}
$builtVersion = Get-ExeVersion (Join-Path $published "Dictum.exe")
if ($builtVersion -ne $appVersion) { throw "dist\Dictum holds version $builtVersion but the project is $($appVersion): run without -NoPublish." }

# 2. The package version: four numbers, each 0-65535.
$parts = @($appVersion.Split('.'))
if ($parts.Count -lt 2 -or $parts.Count -gt 4 -or ($parts | Where-Object { $_ -notmatch '^\d{1,5}$' -or [int]$_ -gt 65535 })) {
    throw "Version '$appVersion' cannot be an MSIX version: two to four numbers, each 0 to 65535."
}
while ($parts.Count -lt 4) { $parts += "0" }
$packageVersion = $parts -join '.'
if ($Store -and [int]$parts[0] -eq 0) {
    throw ("The Microsoft Store rejects package version $packageVersion because its first part is 0 (""App package " +
        "requirements for MSIX app"", Package version numbering, Microsoft Learn). Raise <Version> in Directory.Build.props " +
        "(for example to 1.0.0) or build with -Version.")
}
if ($Store -and [int]$parts[3] -ne 0) { throw "The Microsoft Store reserves the fourth part of the version for itself: it must be 0 (got $packageVersion)." }

# 3. Publisher, and for the test build the signing certificate (in Cert:\CurrentUser\My, named by the .cer).
$cert = $null
if ($Store) {
    $publisher = $storePublisher
} else {
    $cerPath = Join-Path $root "packaging\test-cert\Dictum-Test.cer"
    if (-not (Test-Path -LiteralPath $cerPath)) { throw "No test certificate: run scripts\make-test-cert.ps1 first." }
    $thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $cerPath).Thumbprint
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) { throw "The certificate in $cerPath ($thumbprint) is not in Cert:\CurrentUser\My with its private key: run scripts\make-test-cert.ps1." }
    if ($cert.NotAfter -lt (Get-Date)) { throw "The test certificate expired on $($cert.NotAfter.ToString('dd/MM/yyyy')): run scripts\make-test-cert.ps1 -Force." }
    $publisher = $cert.Subject
}

# 4. Tools: restoring packaging\tools brings the Windows SDK build tools at the version its project pins.
Write-Host "Preparing the packaging tools (packaging\tools)..."
Invoke-Tool $dotnet @("build", $toolsProject, "-c", "Release", "-nologo", "-v", "quiet")
$sdkVersion = (Select-Xml -LiteralPath $toolsProject -XPath "//PackageDownload[@Include='Microsoft.Windows.SDK.BuildTools']").Node.Version.Trim('[', ']')
$sdkBin = Get-ChildItem (Join-Path $root "packaging\out\packages\microsoft.windows.sdk.buildtools\$sdkVersion\bin") -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkBin) { throw "Microsoft.Windows.SDK.BuildTools $sdkVersion is not in packaging\out\packages: the restore of packaging\tools failed." }
$makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
$makepri = Join-Path $sdkBin.FullName "x64\makepri.exe"
$signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"
foreach ($tool in @($makeappx, $makepri, $signtool)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "$tool is missing: the restore of packaging\tools did not bring Microsoft.Windows.SDK.BuildTools $sdkVersion." }
}
Write-Host "  Microsoft.Windows.SDK.BuildTools $sdkVersion (tools $($sdkBin.Name)): makeappx, makepri, signtool"

# 5. The package layout: the published build, minus any .env (secrets placed beside Dictum.exe never ship) and any
#    app-local Visual C++ runtime (below).
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$layout = Join-Path $work "layout"
$priRoot = Join-Path $work "pri"
New-Item -ItemType Directory -Force -Path $layout, (Join-Path $priRoot "Assets") | Out-Null
$secrets = @(Get-ChildItem -LiteralPath $published -Recurse -Force -File -Filter "*.env")
if ($secrets) { Write-Warning "Left out of the package: $($secrets.FullName -join ', ')" }
Get-ChildItem -LiteralPath $published -Force | Where-Object { $_.Name -notlike "*.env" } | Copy-Item -Destination $layout -Recurse -Force
Get-ChildItem -LiteralPath $layout -Recurse -Force -File -Filter "*.env" | Remove-Item -Force
# The Visual C++ runtime comes from Microsoft's framework package, which the manifest declares: a packaged desktop app
# must not carry its own copy ("C++ Runtime framework packages for Desktop Bridge", KB 3176696), so the copies a
# publish.ps1 -VcRuntimeDir build puts beside the native runtimes stay out of the package.
$appLocalVc = @(Get-ChildItem -LiteralPath $layout -Recurse -Force -File | Where-Object { $VcRuntimeDlls -contains $_.Name.ToLowerInvariant() })
if ($appLocalVc) {
    $appLocalVc | Remove-Item -Force
    Write-Host "Left out $($appLocalVc.Count) app-local Visual C++ runtime DLLs: the package uses Microsoft.VCLibs.140.00.UWPDesktop."
}

# 6. Logos, rendered from the SVG mark.
Invoke-Tool $dotnet @("run", "--project", $toolsProject, "-c", "Release", "--no-build", "--", "logos",
    (Join-Path $root "site\dictum.svg"), (Join-Path $root "site\favicon.svg"), (Join-Path $priRoot "Assets"))
Copy-Item -LiteralPath (Join-Path $priRoot "Assets") -Destination $layout -Recurse -Force

# 7. The manifest.
$values = [ordered]@{ IdentityName = $identityName; Publisher = $publisher; Version = $packageVersion; DisplayName = $displayName; PublisherDisplayName = $publisherDisplayName }
$manifest = [IO.File]::ReadAllText($template)
foreach ($key in $values.Keys) { $manifest = $manifest.Replace("{{$key}}", [Security.SecurityElement]::Escape([string]$values[$key])) }
if ($manifest -match '\{\{\w+\}\}') { throw "The manifest template has a token this script does not fill: $($Matches[0])." }
$language = ([xml]$manifest).Package.Resources.Resource.Language
[IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding $false))
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $layout "AppxManifest.xml")
Write-Host "Wrote $manifestPath (Name $identityName, Publisher $publisher, Version $packageVersion, language $language)"

# 8. The resource index that maps Assets\<logo>.png to its scale and target-size files. The default config splits
#    scales into resources.scale-*.pri files, which only a bundle's resource packages use; this single package needs
#    every candidate in one resources.pri, so the <packaging> element is removed ("MakePri.exe configuration file").
$priConfig = Join-Path $work "priconfig.xml"
Write-Host "Indexing the logos (makepri)..."
Invoke-Tool $makepri @("createconfig", "/cf", $priConfig, "/dq", $language, "/pv", "10.0", "/o") "error|warning" -Utf16
$config = [xml][IO.File]::ReadAllText($priConfig)
foreach ($split in @($config.SelectNodes("/resources/packaging"))) { [void]$split.ParentNode.RemoveChild($split) }
$config.Save($priConfig)
Invoke-Tool $makepri @("new", "/pr", $priRoot, "/cf", $priConfig, "/mn", (Join-Path $layout "AppxManifest.xml"), "/of", (Join-Path $layout "resources.pri"), "/o") "Named Resources|Candidates|error|warning" -Utf16
$extraPri = @(Get-ChildItem -LiteralPath $layout -Filter "resources.*.pri")
if ($extraPri) { throw "makepri split the index into $($extraPri.Name -join ', '): the package would lose those logo sizes." }

# 9. Pack, and sign the test build.
$msix = Join-Path $dist "Dictum-$appVersion-$flavour.msix"
Write-Host "Packing $msix (makeappx)..."
Invoke-Tool $makeappx @("pack", "/d", $layout, "/p", $msix, "/o") "^Packing|succeeded|error|warning"
if ($cert) {
    Write-Host "Signing with the test certificate $($cert.Subject) ($($cert.Thumbprint))..."
    Invoke-Tool $signtool @("sign", "/fd", "SHA256", "/sha1", $cert.Thumbprint, "/s", "My", $msix) "Successfully|error|warning"
}

$files = @(Get-ChildItem -LiteralPath $layout -Recurse -File).Count
$size = (Get-Item -LiteralPath $msix).Length
Write-Host ("Built {0}: {1:N0} bytes ({2:0.0} MiB) from {3} files." -f $msix, $size, ($size / 1MB), $files)
if ($cert) {
    $signature = Get-AuthenticodeSignature -LiteralPath $msix
    Write-Host "  Signed by $($signature.SignerCertificate.Subject). Windows reports '$($signature.Status)' until the tester trusts packaging\test-cert\Dictum-Test.cer (docs\TESTING-MSIX.md)."
} else {
    Write-Host "  Unsigned: Partner Center signs Store packages. Upload this file, not the -test one."
}
Write-Host "  Depends on Microsoft.VCLibs.140.00.UWPDesktop: the Store installs it; a sideload needs it first (docs\TESTING-MSIX.md)."
