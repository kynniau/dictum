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

**First, Microsoft's Visual C++ runtime framework package.** Since 17/10/2026 the package declares
`Microsoft.VCLibs.140.00.UWPDesktop` (14.0.33728.0 or later) as a dependency: whisper.cpp and llama.cpp need the Visual
C++ runtime, and a packaged app takes it from that framework package instead of carrying its own copy (docs/DECISIONS.md,
"Visual C++ runtime"). The Store installs it with Dictum; a sideload does not, and without it installation fails with a
dependency error naming `Microsoft.VCLibs` ("MSIX troubleshooting guide", Microsoft Learn, read 17/10/2026). Many PCs
already have it:

```powershell
Get-AppxPackage -Name Microsoft.VCLibs.140.00.UWPDesktop | Select-Object Name, Version, Architecture
```

If no x64 entry of 14.0.33728.0 or later is listed, install Microsoft's framework package first. The troubleshooting
guide names `Microsoft.VCLibs.x64.14.00.Desktop.appx` at https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx (a
Microsoft-signed file: check that its signer is Microsoft Corporation before installing it):

```powershell
Get-AuthenticodeSignature "C:\path\to\Microsoft.VCLibs.x64.14.00.Desktop.appx" | Select-Object Status, SignerCertificate
Add-AppxPackage -Path "C:\path\to\Microsoft.VCLibs.x64.14.00.Desktop.appx"
```

Then Dictum:

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
   `packaged (Kynni.Dictum_<id>)`, not `unpackaged (direct download)`, and its `whisper runtime` and `cleanup backend`
   lines should end in `[OK]` once a model is downloaded (`model load failed` there is the certification failure of
   16/10/2026).
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
package or the install path, and the same Dictate-data warning applies. The framework package must be installed first
there too, from its `.appx` (step 3).

## Windows Sandbox: the clean-PC check

A development PC nearly always has the Visual C++ runtime, so the certification failure of 16/10/2026 only shows on a
clean Windows. `scripts\sandbox-check.ps1` runs `--selftest` inside Windows Sandbox (a clean Windows, discarded when it
closes) and passes only when the speech engine loads, and the clean-up engine when `-CleanupModel` is given:

```powershell
# A published folder (publish.ps1 output, or an unzipped release):
powershell -ExecutionPolicy Bypass -File scripts\sandbox-check.ps1 -Published dist\Dictum
# A package layout, registered in the sandbox's Developer Mode after Microsoft's framework .appx (step 3 above):
powershell -ExecutionPolicy Bypass -File scripts\sandbox-check.ps1 -Package packaging\out\msix-store\layout -Framework C:\path\to\Microsoft.VCLibs.x64.14.00.Desktop.appx
```

The sandbox cannot reach the Store, so it never gets the framework on its own: it is a PC without the framework.
`-Framework` must be Microsoft's signed `.appx` file. Windows registers a framework package from no loose folder
(observed 17/10/2026 with a copy of the build PC's installed framework: in Developer Mode "DevelopmentMode is not
allowed when registering framework or resource packages"; without it "the manifest is not in the package root").
Without the framework, registering the package fails on the dependency, as Microsoft's guide says a sideload does: on
17/10/2026 the sandbox answered
`0x80073CF3`, "Windows cannot install package Kynni.Dictum_1.0.0.0_x64__43s584kx66f64 because this package depends on
a framework that could not be found. Provide the framework "Microsoft.VCLibs.140.00.UWPDesktop" published by "CN=Microsoft
Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", with neutral or x64 processor architecture and
minimum version 14.0.33728.0". The packaged self-test has not run yet: no copy of the framework `.appx` was on the
build PC. The results of 17/10/2026 are in docs/DECISIONS.md, "Visual C++ runtime".
