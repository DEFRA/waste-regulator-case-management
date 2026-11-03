#!/usr/bin/env bash
set -euo pipefail

OLD_PASCAL=EprRegisterEnrolManagementBe
NEW_PASCAL=WasteRegulatorCaseManagement
OLD_KEBAB=epr-register-enrol-management-be
NEW_KEBAB=waste-regulator-case-management

REPO_ROOT=$(git rev-parse --show-toplevel)
NOT_SELF=":(exclude)$(realpath --relative-to="$REPO_ROOT" "$0")"
NOT_ADR=":(exclude)docs/adr"
cd "$REPO_ROOT"

replace_in_tracked_files() {
  git grep -lzIF -e "$1" -- . "$NOT_SELF" "$NOT_ADR" | xargs -0 -r perl -pi -e "s/\Q$1\E/$2/g"
}

new_guid() {
  uuidgen | tr '[:lower:]' '[:upper:]'
}

# Ignored build output carries the old assembly names; it regenerates on next build.
find . -path ./.git -prune -o -type d \( -name bin -o -name obj \) -path "*$OLD_PASCAL*" -print0 \
  | xargs -0 -r rm -rf

# Deepest paths first so a parent rename never invalidates a pending child path.
git ls-files -- . "$NOT_SELF" "$NOT_ADR" | grep -F "$OLD_PASCAL" \
  | awk -F/ '{ for (i = NF; i >= 1; i--) { p = $1; for (j = 2; j <= i; j++) p = p "/" $j; print i "\t" p } }' \
  | grep -F "$OLD_PASCAL" | sort -u | sort -t$'\t' -k1,1nr | cut -f2 \
  | while IFS= read -r path; do
      base=${path##*/}
      [[ -e $path && $base == *"$OLD_PASCAL"* ]] || continue
      dest="$(dirname "$path")/${base//$OLD_PASCAL/$NEW_PASCAL}"
      # git mv into an existing directory nests the source inside it instead of renaming.
      if [[ -e $dest ]]; then
        echo "$dest already exists (untracked or ignored leftovers?); remove it and re-run." >&2
        exit 1
      fi
      git mv "$path" "$dest"
    done

replace_in_tracked_files "$OLD_PASCAL" "$NEW_PASCAL"
replace_in_tracked_files "$OLD_KEBAB" "$NEW_KEBAB"

# Third GUID on each Project(...) line is the project instance GUID; the first is the
# project-type GUID (C#, solution folder) and must not change.
for sln in $(git ls-files '*.sln'); do
  grep -oE '^Project\("\{[^}]+\}"\) = "[^"]+", "[^"]+", "\{[0-9A-Fa-f-]+\}"' "$sln" \
    | grep -oE '\{[0-9A-Fa-f-]{36}\}"$' | tr -d '{}"' \
    | while IFS= read -r old_guid; do
        replace_in_tracked_files "$old_guid" "$(new_guid)"
      done
done

for csproj in $(git ls-files '*.csproj'); do
  old_id=$(grep -oP '(?<=<UserSecretsId>)[^<]+' "$csproj" || true)
  if [ -n "$old_id" ]; then
    perl -pi -e "s/\Q$old_id\E/$(new_guid | tr '[:upper:]' '[:lower:]')/" "$csproj"
  fi
done
