# Contributing to Dictum

Thanks for helping. Dictum is small and deliberate, so please open an issue to talk about a change before you build
anything large.

## Before you start

- `docs/SPEC.md` is the brief and `docs/DECISIONS.md` records every judgement call and why; read the sections your
  change touches. A change in behaviour that the spec does not cover gets a line in DECISIONS.md.
- The privacy promises in the README ("Privacy") are not negotiable: no telemetry, analytics or crash reporting, no
  new network connection without a spec change, nothing leaves the PC by default, and `offline_only` stops every
  connection to another machine. See `.github/SECURITY.md`.
- Platform code lives only in `src/Dictum.Windows` (and later `src/Dictum.Mac`); `src/Dictum.Core` and
  `src/Dictum.UI` reach the operating system only through `IPlatform`, and a test enforces it.
- A new NuGet package needs a one-line reason, the version and where you checked it in the DECISIONS.md tables.

## Build and test

Follow README, "Install" > "Build from source": the .NET 10 SDK, then

```powershell
dotnet build Dictum.sln -c Release
dotnet test --solution Dictum.sln -c Release --no-build
```

Tests that need a model, the desktop, the microphone or the network skip themselves unless their variable is set
(the table in the README). CI (`.github/workflows/ci.yml`) runs the build, the ungated tests and the publish on every
push and pull request; a pull request needs it green. A behaviour change comes with a test that fails without it.

## Style

- Australian English in the UI and the docs, dates as DD/MM/YYYY.
- Say where a fact comes from: "verified" (checked, with the source and date), "recalled" (from memory, unchecked) or
  "observed" (measured here), as DECISIONS.md does.
- Never commit secrets, models or personal data: `.env` files, `*.pfx`, model files (`*.bin`, `*.gguf`) and build
  output are in `.gitignore`. Test text uses made-up names, never real client details.
- Recordings, transcripts and personal test material stay outside the repository. Turn on the guard rails once per clone
  with `git config core.hooksPath .githooks` (the push check needs Git 2.36 or newer), and name your private word
  list with the `DICTUM_PRIVATE_DENYLIST` environment variable or `git config dictum.privateDenylist <path>`. A name
  that points at a file that is not there is refused, and `DICTUM_PRIVATE_DENYLIST=none` skips the check on purpose
  (`.github/SECURITY.md`, "Private material").

## Licence

Dictum is MIT licensed (`LICENSE`, Kynni Pty Ltd). Contributions are accepted under the same licence.
