# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Email **hello@kynni.com.au** with "Security" in the
subject, or use GitHub's private vulnerability reporting (Security > Report a vulnerability) if it is switched on for
this repository. Include the Dictum version (Settings > About, or `Dictum.exe --selftest`), how it was installed
(Microsoft Store, `Dictum-Setup-<version>.exe`, the zip, or a build from source), and the steps that show the problem.
Never send real API keys, recordings or dictated text: describe them instead.

## Supported versions

Dictum is at version 0.1.0 and has no published release yet. Fixes go into the next release; older releases are not
patched.

## What Dictum promises, so what counts as a vulnerability

These are the guarantees the README states ("Privacy"); anything that breaks one is a security bug:

- Nothing leaves the PC by default. Dictum connects only to `huggingface.co` (and its download CDN) when you ask for a
  model, to `dictum.kynni.au` for one small file (`/version.json`, sending nothing about you) when you click Check for
  updates in the direct download, and to a clean-up service off this PC only after you switch it on and agree to that
  host in the consent step. No telemetry, analytics, crash reports or automatic update checks.
- With `offline_only: true`, Dictum itself connects to no other machine.
- Dictation audio stays in memory and is never written to disk; transcripts are not written to disk, and reach the
  log only if you turn on `log_transcripts`.
- API keys come only from environment variables or `.env`, are never written to `settings.json`, the windows or error
  messages, and are redacted from the log.
- Downloaded models are data, checked against pinned SHA-256 hashes where the catalogue has one; Dictum never
  downloads or runs code.
- The global keyboard hook only compares keys with the configured hotkeys and Esc (and, while the "Press your
  hotkey" box is open, shows the key pressed); it stores and sends no keystrokes.

## Release integrity

Release files are built by GitHub Actions (`.github/workflows/ci.yml`) from the tagged commit, and each release lists
their SHA-256 hashes in `SHA256SUMS.txt`. The installer and the zip are not code-signed yet. The test MSIX is signed
with a self-signed certificate made for that build only (its private key is not kept); the Microsoft Store version is
signed by Microsoft.

## Private material

This repository is public, so anything pushed to it is published. Recordings, transcripts and personal test material
(names, phrases, scenarios) live outside the repository, never in it.

`.githooks` stops them being committed or pushed:

- `pre-commit` and `pre-push` refuse audio, video and subtitle files, documents, archives and mail exports (the types
  are listed in `.gitignore`, such as `.wav`, `.m4a`, `.srt`, `.docx`, `.pdf` and `.zip`; wherever they are, in any
  case), anything under a `private/`, `recording/`, `recordings/`, `transcript/` or `transcripts/` folder, any file over
  5 MiB (5,242,880 bytes), and any file name or added line that contains an entry from a denylist you keep outside the
  repository. `commit-msg` refuses a commit message that contains an entry, and `pre-push` also reads the message of
  every commit being pushed and the name of every branch and tag being pushed. They say where something was found (a
  file name, "commit message", "branch name"), never a line of a file or message, or a denylist entry; a name that
  itself holds an entry is shown as is.
- Entries and text are compared after a no-break space, a tab or a run of spaces becomes one space and soft hyphens and
  zero-width characters are removed. Every file is read as text, including one that git would call binary (it holds a
  NUL byte, or has a `-diff` attribute).
- `pre-push` reads every commit being pushed and, for a merge, what the merge itself changed on top of the two sides it
  joined (`git log --diff-merges=remerge`, which needs Git 2.36 or newer; an older Git makes `pre-push` stop, so the push
  is refused). It refuses a merge of three or more parents, whose own changes git cannot show that way, and a tag that
  points at a tree or a file, which has no history to read.
- The denylist is a plain text file with one case-insensitive string per line (blank lines and `#` comments are
  ignored). Name it with the `DICTUM_PRIVATE_DENYLIST` environment variable or, once for every worktree of a clone, with
  `git config dictum.privateDenylist <path>` (`~/` works, and a relative path is read from the top of the worktree you
  are in); the variable wins. With neither set, the hooks skip that one check and say so. If either names a file that
  does not exist or cannot be read, the hooks refuse rather than skip, so a moved file cannot switch the check off; set
  `DICTUM_PRIVATE_DENYLIST=none` to skip it on purpose.
- Turn the hooks on once per clone, for every worktree: `git config core.hooksPath .githooks`.
- `--no-verify` skips them, and a clone without them has no guard. The `private-material` job in
  `.github/workflows/ci.yml` therefore also fails a push or pull request that tracks one of those file types, a private
  folder or a file over 5 MiB. It runs after the push, so it only detects: a public repository is public at push time,
  and the hooks are the real gate.

### Not covered

The guard stops accidents; it does not stop someone who means to get round it.

- `git push --no-verify`, and a clone that has not turned the hooks on. The CI job reads only the tracked files at the
  tip, not the history, and cannot check denylist words, because the list is local.
- Annotated tag messages, and the names and email addresses of a commit's author and committer.
- Content the hooks cannot read as text: UTF-16 text, and anything compressed or encoded. Documents and archives are
  refused by type, but a recording renamed to a type that is not on the list is not.
- Words that only look like an entry: another alphabet, digits for letters, an entry broken over two lines, or a hidden
  character other than the ones listed above.
- `git commit --cleanup=verbatim` (or `whitespace`) given on the command line, and a scissors line typed into a message
  by hand. A hook cannot see command-line flags, so `commit-msg` drops `#` lines that git then keeps, and it stops at a
  scissors line the way git does for `git commit -v`; `pre-push` reads the stored messages and catches both.
