<#
.SYNOPSIS
  Creates the self-signed code-signing certificate that signs Dictum's TEST MSIX (SPEC section 18.1).

.DESCRIPTION
  For testing only: Microsoft Store builds are signed by Microsoft when they are published, and nobody needs this
  certificate to install Dictum from the Store.

  Follows "Create a certificate for package signing" (Microsoft Learn, read 16/10/2026): New-SelfSignedCertificate with
  key usage DigitalSignature, the code-signing extended key usage (1.3.6.1.5.5.7.3.3) and the end-entity basic
  constraint, created in your personal store (Cert:\CurrentUser\My). Then it exports, into packaging\test-cert\
  (ignored by git):
    Dictum-Test.cer                 the public certificate: what a tester trusts
    Dictum-Test.pfx                 the certificate with its private key, protected by a random password
    Dictum-Test.pfx-password.txt    that password (keep the folder private; the .pfx is a portable backup only)
  package-msix.ps1 -Test signs with the certificate in your personal store, found by the .cer's thumbprint.

  This script never changes what Windows trusts. It prints the one command, run once in an elevated PowerShell, that
  makes Windows trust the certificate for MSIX installs (Local Machine > Trusted People, as Microsoft Learn says), and
  the command that removes that trust again.

  Run it again and it reuses the certificate while it is valid; -Force makes a new one (testers then trust the new
  .cer, and the old certificate stays in your store until you remove it).

.PARAMETER Subject
  The certificate subject, which becomes the test package's Publisher. Default "CN=Dictum Test Signing".

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\make-test-cert.ps1
#>
[CmdletBinding()]
param(
    [string]$Subject = "CN=Dictum Test Signing",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$certDir = Join-Path $root "packaging\test-cert"
$cerPath = Join-Path $certDir "Dictum-Test.cer"
$pfxPath = Join-Path $certDir "Dictum-Test.pfx"
$passwordPath = Join-Path $certDir "Dictum-Test.pfx-password.txt"
$codeSigning = "1.3.6.1.5.5.7.3.3"

function Test-Usable($candidate) {
    $candidate.HasPrivateKey -and $candidate.NotAfter -gt (Get-Date).AddDays(7) -and
        ($candidate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq $codeSigning })
}

New-Item -ItemType Directory -Force -Path $certDir | Out-Null
$previous = $null
if (Test-Path -LiteralPath $cerPath) {
    $previous = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $cerPath).Thumbprint
}

$cert = $null
if (-not $Force) {
    # Reuse the certificate the .cer names, else the newest usable one with this subject.
    $mine = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { Test-Usable $_ })
    $cert = $mine | Where-Object { $_.Thumbprint -eq $previous } | Select-Object -First 1
    if (-not $cert) { $cert = $mine | Where-Object { $_.Subject -eq $Subject } | Sort-Object NotAfter -Descending | Select-Object -First 1 }
}

if ($cert) {
    Write-Host "Reusing the test certificate $($cert.Subject), thumbprint $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('dd/MM/yyyy'))."
} else {
    $cert = New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}$codeSigning", "2.5.29.19={text}") -Subject $Subject `
        -FriendlyName "Dictum test package signing (self-signed)"
    Write-Host "Created the test certificate $($cert.Subject), thumbprint $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('dd/MM/yyyy')), in Cert:\CurrentUser\My."
}
if ($cert.Thumbprint -ne $previous) {
    # The backup files belong to the certificate the old .cer named: they are replaced with this one's.
    foreach ($stale in @($pfxPath, $passwordPath)) { if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force } }
}

Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Write-Host "  public certificate: $cerPath"
if (-not (Test-Path -LiteralPath $pfxPath)) {
    # A random password (letters and digits) for the portable .pfx backup; nothing else uses it.
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $plain = [Convert]::ToBase64String($bytes) -replace '[+/=]', ''
    $secure = ConvertTo-SecureString -String $plain -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
    Set-Content -LiteralPath $passwordPath -Value $plain -Encoding ASCII
    Write-Host "  private key backup: $pfxPath (password in $passwordPath; both are ignored by git)"
}

Write-Host ""
Write-Host "To install the test MSIX, a tester trusts this certificate once, in an ELEVATED PowerShell (Run as administrator):"
Write-Host "  Import-Certificate -FilePath `"$cerPath`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host "To remove that trust again when testing is over (elevated):"
Write-Host "  Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq '$($cert.Thumbprint)' | Remove-Item"
