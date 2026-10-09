#!/usr/bin/env bash

set -euo pipefail

# Scan tracked and untracked non-ignored working-tree content and print filenames, never matching lines. This is
# intentionally conservative: a finding must be reviewed or removed before it
# can enter the repository. Local .env files are ignored and are not scanned.

assignment_pattern='(BINANCE_API_KEY|BINANCE_API_SECRET|AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN)[[:space:]]*=[[:space:]]*(?!replace-locally|<[^>]+>|\$\{[^}]+\})[^[:space:]#]{8,}'
token_pattern='(AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{20,})'
private_key_pattern='-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----'

if ! command -v rg >/dev/null 2>&1; then
  echo 'Secret scan cannot run: required ripgrep executable (rg) is missing.' >&2
  exit 127
fi
for executable in git sort mktemp; do
  if ! command -v "$executable" >/dev/null 2>&1; then
    echo "Secret scan cannot run: required executable ($executable) is missing." >&2
    exit 127
  fi
done

file_list="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-secret-scan-files.XXXXXX")"
trap 'rm -f -- "$file_list"' EXIT
if ! git -c core.fsmonitor=false ls-files --cached --others --exclude-standard -z | sort -zu >"$file_list"; then
  echo 'Secret scan could not enumerate repository files.' >&2
  exit 1
fi

declare -a findings=()

while IFS= read -r -d '' file; do
  [[ -f "$file" ]] || continue
  [[ "$file" != .env && "$file" != */.env && "$file" != .env.* && "$file" != */.env.* ]] || continue
  set +e
  rg -l -P -- "$assignment_pattern|$token_pattern|$private_key_pattern" "$file" >/dev/null
  rg_status=$?
  set -e
  if (( rg_status == 0 )); then
    findings+=("$file")
  elif (( rg_status != 1 )); then
    echo "Secret scan failed while scanning a repository file (rg exit $rg_status)." >&2
    exit "$rg_status"
  fi
done <"$file_list"

if ((${#findings[@]} > 0)); then
  echo "Potential secret material found in working-tree file(s):"
  printf ' - %s\n' "${findings[@]}"
  echo "Remove the material and rotate any credential before retrying. Matching lines are intentionally not printed."
  exit 1
fi

echo "Secret scan passed: no supported credential patterns found in non-ignored working-tree files."
