#!/bin/sh
# Private-material guard. Sourced by .githooks/pre-commit and .githooks/pre-push, and by the CI job "private-material"
# (.github/workflows/ci.yml); never run on its own. This repository is public, so a recording, a transcript or a
# personal test phrase is published the moment it is pushed. Turn the hooks on once per clone, for every worktree:
#   git config core.hooksPath .githooks
#
# Three checks on the files a commit or push would add. Output names files only: never their contents, and never
# the text that matched.
#
#   1. Path      A media file (list below), or anything under a private/, recordings/ or transcripts/ folder.
#                Matched case-insensitively. Like .gitignore, a .wav directly in tests/fixtures/ is allowed.
#   2. Size      A file over 5 MiB (recording-sized).
#   3. Denylist  A file name or an ADDED line containing a string from a local denylist file that is never committed:
#                one case-insensitive fixed string per line; blank lines and lines starting with # are ignored.
#                Read from $DICTUM_PRIVATE_DENYLIST, else from "Dictum private/denylist.txt" in the folder that holds
#                the main checkout (found through the common git dir, so every worktree uses the same file). With no
#                file the check is skipped and one line says so. Binary files are not scanned for text.
#
# A hook defines changes(): it runs git with the options it is given, plus whatever selects the changes to inspect
# (the staged changes, or the commits being pushed). Then it calls pg_setup, pg_load_denylist and pg_run.

PG_MAX_BYTES=5242880   # 5 MiB; keep the "over 5 MB" label in pg_flag_blobs in step
PG_MEDIA_EXTS='wav|mp4|m4a|mp3|ogg|oga|opus|flac|aac|wma|webm|3gp|amr|mov|mkv|avi|aiff|caf'   # keep in step with .gitignore

pg_die() { echo "private-guard: $*" >&2; exit 1; }

# A scratch folder, removed on exit. GIT_LITERAL_PATHSPECS: a file name is never read as a glob.
pg_setup() {
  PG_TMP=$(mktemp -d) || pg_die "cannot make a temporary folder"
  trap 'rm -rf "$PG_TMP"' EXIT
  trap 'exit 1' HUP INT TERM
  GIT_LITERAL_PATHSPECS=1; export GIT_LITERAL_PATHSPECS
  : > "$PG_TMP/flagged"
}

# Checks 1 and 2. $1 is a file of "<blob id> <path>" lines. Each file that fails is appended to $PG_TMP/flagged as
# "reason<TAB>path". git's object database gives the sizes: one process, however many files.
pg_flag_blobs() {
  git cat-file --batch-check='%(objecttype) %(objectsize) %(rest)' < "$1" > "$PG_TMP/sizes" || pg_die "git could not read the file sizes"
  awk -v exts="$PG_MEDIA_EXTS" -v max="$PG_MAX_BYTES" '
    BEGIN {
      media = "[.](" exts ")$"
      fixture = "^tests/fixtures/[^/]+[.]wav$"
      folder = "(^|/)(private|recordings|transcripts)/"
    }
    $1 == "blob" {                      # any other type is a submodule commit, which is not a file here
      path = $0; sub(/^[^ ]+ [^ ]+ /, "", path)
      name = path; if (name ~ /^".*"$/) name = substr(name, 2, length(name) - 2)   # git quotes unusual names
      low = tolower(name)
      if (low ~ media && low !~ fixture) print "media file\t" path
      if (low ~ folder)                  print "private folder\t" path
      if ($2 + 0 > max)                  print "over 5 MB\t" path
    }' "$PG_TMP/sizes" >> "$PG_TMP/flagged" || pg_die "could not check the file names and sizes"
}

# Checks 1 and 2 on what changes() selects. Deleted files are skipped: removing a bad file is always allowed.
pg_check_files() {
  changes --raw --no-abbrev --diff-filter=d > "$PG_TMP/raw" || pg_die "git could not list the changed files"
  awk -F'\t' '/^:/ { split($1, m, " "); print m[4], $2 }' "$PG_TMP/raw" > "$PG_TMP/blobs" || pg_die "could not read the changed files"
  pg_flag_blobs "$PG_TMP/blobs"
}

# Check 3, set-up. Leaves PG_DENY as the cleaned list of strings, or empty when the check is skipped.
pg_load_denylist() {
  PG_DENY=
  list=${DICTUM_PRIVATE_DENYLIST:-}
  if [ -z "$list" ]; then
    common=$(cd "$(git rev-parse --git-common-dir)" && pwd) || pg_die "not inside a git repository"
    list="$(cd "$common/../.." && pwd)/Dictum private/denylist.txt"
  fi
  if [ ! -e "$list" ]; then
    echo "private-guard: no denylist at $list; skipping the denylist check." >&2
    return 0
  fi
  [ -f "$list" ] && [ -r "$list" ] || pg_die "cannot read the denylist at $list"
  # UTF-16 (what some editors and PowerShell redirection write) would load but never match: refuse instead.
  tr -d '\000' < "$list" | cmp -s - "$list" || pg_die "the denylist at $list is not plain text (UTF-16?); save it as UTF-8"
  # Drop a UTF-8 byte-order mark, trim each line (this also removes CR), then drop comments and blank lines:
  # an empty string would match every line.
  bom=$(printf '\357\273\277')
  sed -e "1s/^$bom//" -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^#/d' -e '/^$/d' "$list" > "$PG_TMP/deny" \
    || pg_die "cannot read the denylist at $list"
  if [ -s "$PG_TMP/deny" ]; then
    PG_DENY=$PG_TMP/deny
  else
    echo "private-guard: the denylist at $list has no entries; skipping the denylist check." >&2
  fi
}

# Check 3 on what changes() selects. One grep over every file name and added line says whether anything matches;
# only then is each file tried on its own, to name the culprits. Added lines only: a line already in the repository
# never blocks an edit to its file (a renamed file counts as added, because the diff is taken without rename detection).
pg_check_denylist() {
  [ -n "$PG_DENY" ] || return 0
  changes --name-only -z --diff-filter=d > "$PG_TMP/names0" || pg_die "git could not list the changed files"
  tr '\0' '\n' < "$PG_TMP/names0" | sort -u > "$PG_TMP/names"
  changes -p -U0 > "$PG_TMP/patch" || pg_die "git could not read the changes"
  sed -n 's/^+//p' "$PG_TMP/patch" > "$PG_TMP/added"
  grep -qiaFf "$PG_DENY" "$PG_TMP/names" "$PG_TMP/added"
  case $? in 0) ;; 1) return 0 ;; *) pg_die "grep failed on the denylist" ;; esac
  found=
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    if { printf '%s\n' "$file"; changes -p -U0 -- "$file" | sed -n 's/^+//p'; } | grep -qiaFf "$PG_DENY"; then
      printf 'local denylist\t%s\n' "$file" >> "$PG_TMP/flagged"
      found=1
    fi
  done < "$PG_TMP/names"
  [ -n "$found" ] || printf 'local denylist\t(a changed line; no single file found)\n' >> "$PG_TMP/flagged"
}

# Print the flagged files and exit 1; does nothing when none is flagged. $1 is commit, push or check (the CI audit).
pg_refuse_if_flagged() {
  [ -s "$PG_TMP/flagged" ] || return 0
  case $1 in
    commit) pg_head="commit refused. These files must not go into a public repository:"
            pg_foot="Unstage them (git restore --staged <file>) and keep private material outside the repository." ;;
    push)   pg_head="push refused. These files must not go into a public repository:"
            pg_foot="They are in commits that would be pushed. Take them out of those commits, then push again." ;;
    *)      pg_head="these tracked files must not be in a public repository:"
            pg_foot="Remove them from the repository, and treat anything already pushed as published." ;;
  esac
  echo "private-guard: $pg_head" >&2
  sort -u "$PG_TMP/flagged" | awk -F'\t' '{ printf "  %-15s %s\n", $1, $2 }' >&2
  echo "$pg_foot" >&2
  exit 1
}

# Run the checks on what changes() selects. The cheap ones go first, so a huge file is refused before it is diffed.
pg_run() {
  pg_check_files
  pg_refuse_if_flagged "$1"
  pg_check_denylist
  pg_refuse_if_flagged "$1"
}

# CI, after the fact: checks 1 and 2 on every tracked file. Check 3 needs the local denylist, so it cannot run there.
pg_audit_tracked_files() {
  pg_setup
  git -c core.quotePath=false ls-files -s > "$PG_TMP/index" || pg_die "git could not list the tracked files"
  awk -F'\t' '{ split($1, m, " "); print m[2], $2 }' "$PG_TMP/index" > "$PG_TMP/blobs" || pg_die "could not read the tracked files"
  pg_flag_blobs "$PG_TMP/blobs"
  pg_refuse_if_flagged check
}
