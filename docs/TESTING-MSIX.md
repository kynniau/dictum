# Testing the MSIX package

For testers only. People installing Dictum from the Microsoft Store need none of this: Store packages are signed by
Microsoft when they are published.

The test package is signed with a self-signed certificate made on the build machine, so Windows refuses it
(`0x800B0109`, "the root certificate ... must be trusted") until the tester trusts that certificate once. Trusting a
certificate changes what the whole PC trusts, so it needs an elevated PowerShell and a person's decision; no script in
this repository does it.

Written 16/10/2026 for Dictum 1.0.0. Microsoft sources (Microsoft Learn, read 16/10/2026): "Create a certificate for
package signing" (trust goes in Local Machine > Trusted People), "MSIX troubleshooting guide" (App Installer checks only
the machine store; never import a self-signed certificate into Trusted Root), "Understanding how packaged desktop apps
run on Windows" (what happens to app data).

## Before you start: the earlier build's data

**Do not install the test package in a Windows user account that still has data from the earlier build, Dictate**
(`%APPDATA%\Dictate` or `%LOCALAPPDATA%\Dictate`), unless you have copied those two folders somewhere safe first.

Why: on its first start Dictum moves Dictate's settings, models and recordings into its own folders (README, "Moving
from Dictate"). In a packaged app, Windows writes newly created files under AppData to a private, per-package folder,
allows deletes of existing files, and removes the private folder when the package is uninstalled (Microsoft Learn,
above). So a packaged first start could leave those files only inside the package's private folder, and uninstalling
the test package would then delete them. This is inferred from Microsoft's description, not observed. The safest test
is in a separate Windows user account, a virtual machine, or Windows Sandbox.

## 1. Build the package (build machine, normal PowerShell)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-test-cert.ps1      # once; reused while valid
powershell -ExecutionPolicy Bypass -File scripts\package-msix.ps1 -Test
```

Output: `dist\Dictum-<version>-test.msix` and the public certificate `packaging\test-cert\Dictum-Test.cer`. A tagged
GitHub release carries both (`Dictum-<version>-test.msix`, `Dictum-<version>-test.cer`); a release's certificate is
made for that release's build, so trust the `.cer` that came with the package you install.

## 2. Trust the test certificate (tester's PC, ELEVATED PowerShell, once)

Open PowerShell with **Run as administrator**, then, with the path to the `.cer` you were given:

```powershell
Import-Certificate -FilePath "C:\path\to\Dictum-Test.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

`make-test-cert.ps1` prints this command with the full path filled in. It adds the certificate to Local Machine >
Trusted People, and from then on Windows installs packages signed with it. Microsoft notes that a certificate in a
local machine store affects certificate trust for every user of the PC, which is why step 5 removes it again.

## 3. Install and check (normal PowerShell)

```powershell
Add-AppxPackage -Path "C:\path\to\Dictum-1.0.0-test.msix"     # or double-click the .msix and choose Install
Get-AppxPackage -Name Kynni.Dictum | Select-Object Name, Version, Publisher, PackageFamilyName, InstallLocation
```

The test package's Publisher is `CN=Dictum Test Signing`; the Store package's is Partner Center's
`CN=CDA37BA0-481B-4F79-9D16-A8AD5ABB1D76`. Both are named `Kynni.Dictum`; their different publishers give them
different package family names.

Then:

1. Start **Dictum** from the Start menu. Settings > About says whether it runs as an installed package.
2. Self-test from a terminal through the package's alias, with package identity: `Dictum.exe --selftest | Out-Host`
   (the alias lives in `%LOCALAPPDATA%\Microsoft\WindowsApps`, which is on PATH). Its `build` line should read
   `packaged (Kynni.Dictum_<id>)`, not `unpackaged (direct download)`.
3. Dictate once into Notepad (hold the hotkey, speak, let go).
4. Settings > General > Launch at login: turn it on, then check Task Manager > Startup apps lists Dictum; turn it off.
5. Settings > Storage shows the real folders (the package's private copies under
   `%LOCALAPPDATA%\Packages\Kynni.Dictum_<id>\LocalCache` once Dictum has written there).

## 4. Uninstall and confirm nothing is left

Settings > Apps > Installed apps > Dictum > Uninstall (or `Get-AppxPackage -Name Kynni.Dictum | Remove-AppxPackage`).
Then each of these should come back empty or `False`:

```powershell
Get-AppxPackage -Name Kynni.Dictum
Test-Path "$env:LOCALAPPDATA\Packages\Kynni.Dictum_*"
Test-Path "$env:LOCALAPPDATA\Microsoft\WindowsApps\Dictum.exe"
```

## 5. Remove the trust when testing is over (ELEVATED PowerShell)

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq "CN=Dictum Test Signing" | Remove-Item
```

Microsoft recommends removing such certificates when they are no longer needed.

## Status on the build machine (16/10/2026)

- Built and signed: `dist\Dictum-1.0.0-test.msix`, 105,894,052 bytes, signer `CN=Dictum Test Signing`.
- `Add-AppxPackage` without the trust step failed as expected with `0x800B0109` and installed nothing.
- Steps 2 to 5 were **not run**: the shell had no administrator rights, and trusting a certificate is the
  tester's decision. Nothing here has run as a package yet.

A developer alternative that needs no certificate, Developer Mode's `Add-AppxPackage -Register` of the unpacked
layout (`packaging\out\msix-test\layout\AppxManifest.xml`), tests the app with package identity but not the signed
package or the install path, and the same Dictate-data warning applies.
