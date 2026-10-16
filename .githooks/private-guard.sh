#!/bin/sh
# Private-material guard. Sourced by .githooks/pre-commit, .githooks/commit-msg and .githooks/pre-push, and by the CI job
# "private-material" (.github/workflows/ci.yml); never run on its own. This repository is public, so a recording, a
# transcript or a personal test phrase is published the moment it is pushed. Turn the hooks on once per clone, for
# every worktree:
#   git config core.hooksPath .githooks
#
# Three checks. Output says where something was found (a file name, "commit message", "branch name"), never a line of a
# file or message, or a denylist entry; a name that itself holds an entry is shown as is.
#
#   1. Path      A blocked file type (list below: audio, video, subtitles, documents, archives, mail exports), or
#                anything under a private/, recording(s)/ or transcript(s)/ folder. Matched case-insensitively.
#                No exception: a .wav is refused wherever it is.
#   2. Size      A file over 5 MiB (5,242,880 bytes; recording-sized).
#   3. Denylist  Text containing a string from a local denylist file that is never committed: one case-insensitive
#                fixed string per line; blank lines and lines starting with # are ignored. Entries and text are both
#                normalised before they are compared (pg_normalise), so a no-break space, a tab, a run of spaces, a
#                soft hyphen or a zero-width character cannot hide an entry. The text checked is a file name or an
#                ADDED line (pre-commit, pre-push; every file is read as text, binary or not), a commit message
#                (commit-msg; pre-push, for every commit being pushed), and the name of a branch or tag being pushed
#                (pre-push). The file is named by $DICTUM_PRIVATE_DENYLIST, else by the git config key
#                dictum.privateDenylist (set once, for every worktree of a clone: git config dictum.privateDenylist
#                <path>; read as a path, so ~/ works, and a relative path is relative to the root of the worktree git
#                is working in). With neither set, the check is skipped and one line says so. If either names a file
#                that does not exist or cannot be read, the hook refuses: a moved file or a typo must not switch the
#                check off. To skip it on purpose, set DICTUM_PRIVATE_DENYLIST=none (or the key to none).
#
# A hook defines changes(): it runs git with the options it is given, plus whatever selects the changes to inspect
# (the staged changes, or the commits being pushed). Then it calls pg_setup, pg_load_denylist and pg_run.

PG_MAX_BYTES=5242880   # 5 MiB; keep the "over 5 MiB" label in pg_flag_blobs in step
# Audio, video and subtitles, then documents, archives and mail exports (they can hold a recording or a transcript, and
# their contents are not read). Keep in step with .gitignore.
PG_MEDIA_EXTS='wav|mp4|m4a|mp3|ogg|oga|opus|flac|aac|wma|webm|3gp|amr|mov|mkv|avi|aiff|caf|aif|wmv|m4v|mpg|mpeg|m4b|mka|weba|spx|flv|3g2|pcm|dss|ds2|srt|vtt|docx|doc|rtf|odt|pdf|zip|7z|rar|msg|eml'

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
      folder = "(^|/)(private|recordings?|transcripts?)/"
    }
    $1 == "blob" {                      # any other type is a submodule commit, which is not a file here
      path = $0; sub(/^[^ ]+ [^ ]+ /, "", path)
      name = path; if (name ~ /^".*"$/) name = substr(name, 2, length(name) - 2)   # git quotes unusual names
      low = tolower(name)
      if (low ~ media)                   print "blocked type\t" path
      if (low ~ folder)                  print "private folder\t" path
      if ($2 + 0 > max)                  print "over 5 MiB\t" path
    }' "$PG_TMP/sizes" >> "$PG_TMP/flagged" || pg_die "could not check the file names and sizes"
}

# Checks 1 and 2 on what changes() selects. Deleted files are skipped: removing a bad file is always allowed.
pg_check_files() {
  changes --raw --no-abbrev --diff-filter=d > "$PG_TMP/raw" || pg_die "git could not list the changed files"
  awk -F'\t' '/^:/ { split($1, m, " "); print m[4], $2 }' "$PG_TMP/raw" > "$PG_TMP/blobs" || pg_die "could not read the changed files"
  pg_flag_blobs "$PG_TMP/blobs"
}

# The characters that hide a word from a plain substring match, as the UTF-8 bytes sed will be given: a no-break space
# (U+00A0), a soft hyphen (U+00AD), and the zero-width space, non-joiner and joiner (U+200B to U+200D). One process
# makes all five.
pg_init_normaliser() {
  IFS=' ' read -r PG_NBSP PG_SHY PG_ZWSP PG_ZWNJ PG_ZWJ <<PG_EOF
$(printf '\302\240 \302\255 \342\200\213 \342\200\214 \342\200\215')
PG_EOF
}

# Writes the files $@ with those characters dealt with: a no-break space, a tab or a run of spaces becomes one space, and
# the soft hyphen and the zero-width characters are deleted. Bytes, in the C locale, so it behaves the same on every
# system and binary input passes through. Lines are never joined: an entry broken across two lines is not found.
pg_normalise() {
  LC_ALL=C sed -e "s/$PG_NBSP/ /g" -e "s/$PG_SHY//g" -e "s/$PG_ZWSP//g" -e "s/$PG_ZWNJ//g" -e "s/$PG_ZWJ//g" \
    -e 's/[[:blank:]][[:blank:]]*/ /g' "$@"
}

# Check 3's matcher: is any denylist entry in the files $@? Entries were normalised when they were loaded; the text is
# here. Returns 0 for yes, 1 for no, and 2 when it could not tell (the callers treat that as a hit, or stop).
pg_hits() {
  pg_normalise "$@" > "$PG_TMP/norm" || return 2
  grep -qiaFf "$PG_DENY" "$PG_TMP/norm"
}

# The added lines of the patch on stdin, without the leading +. The C locale: a binary file read as text must not upset
# a sed that dislikes invalid UTF-8.
pg_added_lines() { LC_ALL=C sed -n 's/^+//p'; }

# Check 3, set-up. Leaves PG_DENY as the cleaned list of strings, or empty when the check is skipped. With $1 = quiet,
# says nothing when the check is skipped (commit-msg: pre-commit has just said so). A denylist that is named but cannot
# be read stops the hook whatever $1 is: a check that quietly does not run is worse than one that refuses.
pg_load_denylist() {
  PG_DENY=
  list=${DICTUM_PRIVATE_DENYLIST:-}
  if [ -z "$list" ]; then
    list=$(git config --type=path --get dictum.privateDenylist)
    case $? in 0|1) ;; *) pg_die "git cannot read the config key dictum.privateDenylist (is it a path git can expand?)" ;; esac
  fi
  if [ -z "$list" ]; then
    [ "$1" = quiet ] || echo "private-guard: no denylist is set (DICTUM_PRIVATE_DENYLIST or git config dictum.privateDenylist); skipping the denylist check." >&2
    return 0
  fi
  if [ "$list" = none ]; then
    [ "$1" = quiet ] || echo "private-guard: the denylist is set to none; skipping the denylist check." >&2
    return 0
  fi
  [ -e "$list" ] || pg_die "the denylist at $list does not exist (named by DICTUM_PRIVATE_DENYLIST or git config dictum.privateDenylist). Fix the path, or set DICTUM_PRIVATE_DENYLIST=none to skip the denylist check on purpose."
  [ -f "$list" ] && [ -r "$list" ] || pg_die "cannot read the denylist at $list"
  # UTF-16 (what some editors and PowerShell redirection write) would load but never match: refuse instead.
  tr -d '\000' < "$list" | cmp -s - "$list" || pg_die "the denylist at $list is not plain text (UTF-16?); save it as UTF-8"
  # Drop a UTF-8 byte-order mark, normalise (so an entry typed with a no-break space or two spaces matches), trim each
  # line (this also removes CR), then drop comments and blank lines: an empty string would match every line.
  bom=$(printf '\357\273\277')
  pg_init_normaliser
  sed -e "1s/^$bom//" "$list" > "$PG_TMP/deny0" || pg_die "cannot read the denylist at $list"
  pg_normalise "$PG_TMP/deny0" > "$PG_TMP/deny1" || pg_die "cannot read the denylist at $list"
  sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^#/d' -e '/^$/d' "$PG_TMP/deny1" > "$PG_TMP/deny" \
    || pg_die "cannot read the denylist at $list"
  if [ -s "$PG_TMP/deny" ]; then
    PG_DENY=$PG_TMP/deny
  elif [ "$1" != quiet ]; then
    echo "private-guard: the denylist at $list has no entries; skipping the denylist check." >&2
  fi
}

# Check 3 on what changes() selects. One grep over every file name and added line says whether anything matches;
# only then is each file tried on its own, to name the culprits. Added lines only: a line already in the repository
# never blocks an edit to its file (a renamed file counts as added, because the diff is taken without rename detection).
# --text: a file git would call binary (a NUL byte, or a -diff attribute) is read as text like any other.
pg_check_denylist() {
  [ -n "$PG_DENY" ] || return 0
  changes --name-only -z --diff-filter=d > "$PG_TMP/names0" || pg_die "git could not list the changed files"
  tr '\0' '\n' < "$PG_TMP/names0" | sort -u > "$PG_TMP/names"
  changes -p -U0 --text > "$PG_TMP/patch" || pg_die "git could not read the changes"
  pg_added_lines < "$PG_TMP/patch" > "$PG_TMP/added" || pg_die "could not read the changes"
  pg_hits "$PG_TMP/names" "$PG_TMP/added"
  case $? in 0) ;; 1) return 0 ;; *) pg_die "grep failed on the denylist" ;; esac
  found=
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    changes -p -U0 --text -- "$file" > "$PG_TMP/filepatch" || pg_die "git could not read the changes to $file"
    { printf '%s\n' "$file"; pg_added_lines < "$PG_TMP/filepatch"; } > "$PG_TMP/one"
    if pg_hits "$PG_TMP/one"; then
      printf 'local denylist\t%s\n' "$file" >> "$PG_TMP/flagged"
      found=1
    fi
  done < "$PG_TMP/names"
  [ -n "$found" ] || printf 'local denylist\t(a changed line; no single file found)\n' >> "$PG_TMP/flagged"
}

# Check 3 on a text file ($2), such as a commit message. $1 says where the text came from. If it holds a denylist entry,
# record $1 as flagged and return 0; return 1 when it is clean. Never prints the text. A match that fails counts as a
# hit: refuse rather than let the text through.
pg_flag_text() {
  [ -n "$PG_DENY" ] || return 1
  pg_hits "$2"
  case $? in
    1) return 1 ;;
    0) printf 'local denylist\t%s\n' "$1" >> "$PG_TMP/flagged" ;;
    *) printf 'local denylist\t%s (could not be checked)\n' "$1" >> "$PG_TMP/flagged" ;;
  esac
}

# Check 3 on the messages of the commits changes() selects (pre-push). One grep over all of them says whether any
# matches; only then is each commit tried on its own, to name the culprits by short id.
pg_check_messages() {
  [ -n "$PG_DENY" ] || return 0
  changes --diff-merges=off --format=%B > "$PG_TMP/messages" || pg_die "git could not read the commit messages"
  pg_hits "$PG_TMP/messages"
  case $? in 0) ;; 1) return 0 ;; *) pg_die "grep failed on the denylist" ;; esac
  changes --diff-merges=off --format=%H > "$PG_TMP/ids" || pg_die "git could not list the commits"
  found=
  while IFS= read -r id; do
    [ -n "$id" ] || continue
    git -c log.showSignature=false log -1 --format=%B "$id" > "$PG_TMP/message" || pg_die "git could not read the commit $id"
    if pg_flag_text "commit message ($(printf '%.7s' "$id"))" "$PG_TMP/message"; then found=1; fi
  done < "$PG_TMP/ids"
  [ -n "$found" ] || printf 'local denylist\t(a commit message; no single commit found)\n' >> "$PG_TMP/flagged"
}

# Merges of three or more parents (pre-push). git can remerge a two-parent merge to show what the merge itself added, but
# not an octopus merge (it prints a warning where the diff would be), so what one adds on its own would reach no other
# check: refuse it, by short id. --diff-merges=off here and in the two listings above keeps that warning out of the output.
pg_check_octopus() {
  changes --diff-merges=off --min-parents=3 --format=%H > "$PG_TMP/octopus" || pg_die "git could not list the merge commits"
  while IFS= read -r id; do
    [ -n "$id" ] || continue
    printf 'octopus merge\tcommit %.7s\n' "$id" >> "$PG_TMP/flagged"
  done < "$PG_TMP/octopus"
}

# Check 3 on the name a branch or tag will have on the remote (pre-push). $1 is the full ref name.
pg_check_ref() {
  case $1 in
    refs/heads/*) pg_kind="branch name"; pg_name=${1#refs/heads/} ;;
    refs/tags/*)  pg_kind="tag name";    pg_name=${1#refs/tags/} ;;
    *)            pg_kind="ref name";    pg_name=$1 ;;
  esac
  printf '%s\n' "$pg_name" > "$PG_TMP/ref"
  pg_flag_text "$pg_kind ($pg_name)" "$PG_TMP/ref" || true
}

# The text of the commit message file $1 as git will commit it. Written without an editor (-m, -F, --no-edit, a merge:
# git sets GIT_EDITOR to ":") the file is the message as it stands, # lines included. Written with one, git drops the #
# lines unless commit.cleanup is whitespace, verbatim or scissors, and cuts the message at the scissors line when the
# editor shows the diff (git commit -v puts it there). The hook always cuts there, so it misses only text under a scissors
# line typed by hand. All of this was read from real git output. If a comment character is configured, read everything:
# that can only over-match. The command line is invisible to a hook, so git commit --cleanup=verbatim is not seen;
# pre-push reads the stored messages and catches it.
pg_message_text() {
  if [ "${GIT_EDITOR:-}" = ":" ] || [ -n "$(git config --get core.commentChar)$(git config --get core.commentString)" ]; then
    cat "$1"
    return
  fi
  case $(git config --get commit.cleanup) in
    verbatim|whitespace|scissors) sed -e '/^# -* >8 -*$/,$d' "$1" ;;
    *)                            sed -e '/^# -* >8 -*$/,$d' -e '/^#/d' "$1" ;;
  esac
}

# Print what was flagged and exit 1; does nothing when nothing is flagged. $1 is commit (pre-commit), message
# (commit-msg), push (pre-push) or check (the CI audit).
pg_refuse_if_flagged() {
  [ -s "$PG_TMP/flagged" ] || return 0
  case $1 in
    commit)  pg_head="commit refused. These files must not go into a public repository:"
             pg_foot="Unstage them (git restore --staged <file>) and keep private material outside the repository." ;;
    message) pg_head="commit refused. This text must not go into a public repository:"
             pg_foot="Reword the commit message without it, then commit again." ;;
    push)    pg_head="push refused. These must not go into a public repository:"
             pg_foot="They are in commits, branch names or tag names that would be pushed, or cannot be read (a ref that is not a commit, an octopus merge). Take them out (reword, rebase, rename), then push again." ;;
    *)       pg_head="these tracked files must not be in a public repository:"
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
