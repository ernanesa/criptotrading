#!/usr/bin/env bash
set -Eeuo pipefail

# Composite local recovery gate. Each child drill owns its own ephemeral
# resources; this runner only aggregates exit status and bounded metadata.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="${RECOVERY_REPORT_DIR:-$repo_root/artifacts/recovery}"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-full-recovery.XXXXXX")"
cleanup() { rm -rf -- "$work_dir"; }
trap cleanup EXIT

diagnostics_dir="${RECOVERY_DIAGNOSTICS_DIR:-$report_dir/diagnostics-$run_stamp-$$}"
diagnostics_self_test=false
if [[ "${1:-}" == "--self-test-failure-path" && $# == 1 ]]; then
    diagnostics_self_test=true
elif (( $# > 0 )); then
    echo "Usage: $0 [--self-test-failure-path]" >&2
    exit 2
fi
umask 077
if ! python3 - "$report_dir" <<'PY'
import os
import sys

path = os.path.abspath(sys.argv[1])
sys.exit(0 if os.path.realpath(path) == path else 1)
PY
then
    echo "Recovery report path must not contain symlink components: $report_dir" >&2
    exit 2
fi
if [[ -L "$report_dir" ]]; then
    echo "Recovery report directory must not be a symlink: $report_dir" >&2
    exit 2
fi
mkdir -p -- "$report_dir"
report_file="$(mktemp "$report_dir/full-recovery-$run_stamp-XXXXXX.md")"

dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ "$diagnostics_self_test" == true ]]; then
    declare -a names=(diagnostics-fixture)
    declare -a commands=()
else
    declare -a names=(ledger-recovery full-state-manifest postgres-integration postgres-backup-restore)
    declare -a commands=(
        "scripts/test-ledger-recovery.sh"
        "full-state-manifest"
        "scripts/test-postgres-integration.sh"
        "scripts/test-postgres-backup-restore.sh"
    )
fi
declare -a statuses=()
declare -a durations=()

for index in "${!names[@]}"; do
    started="$(date +%s%N)"
    output_file="$work_dir/${names[$index]}.log"
    status=0
    if [[ "$diagnostics_self_test" == true ]]; then
        if (python3 - <<'PY'
print('diagnostics fixture: password=TEST_ONLY_SENTINEL token=FAKE_SECRET')
print('json fixture: {"client_secret":"JSON_SECRET"}')
print('uri fixture: postgresql://user:URI_SECRET@db:5432/trades')
print('A marker line without credentials.')
print('oversized fixture: ' + ('x' * 20000) + ' password=LONG_TAIL_SECRET')
PY
            exit 42) >"$output_file" 2>&1; then
            status=0
        else
            status=$?
        fi
    elif (( index == 1 )); then
        if (cd "$repo_root" && "$dotnet_command" test tests/CryptoTrading.Persistence.Tests/CryptoTrading.Persistence.Tests.csproj \
            --no-restore --configuration Release --minimum-expected-tests 1 \
            -- --filter-class CryptoTrading.Persistence.Tests.FullStateReconstructionTests >"$output_file" 2>&1); then
            status=0
        else
            status=$?
        fi
    elif (cd "$repo_root" && "${repo_root}/${commands[$index]}" >"$output_file" 2>&1); then
        status=0
    else
        status=$?
    fi
    finished="$(date +%s%N)"
    statuses+=("$status")
    durations+=("$(( (finished - started) / 1000000 ))")
done

failed=0
for status in "${statuses[@]}"; do
    if [[ "$status" != 0 ]]; then
        failed=1
    fi
done

cat > "$report_file" <<EOF
# Full local recovery drill — $run_stamp

- Scope: deterministic local recovery components only; no Binance credentials or exchange calls.
- Composite gate: ledger reconstruction, full-state manifest, PostgreSQL integration/restart, and ephemeral backup/restore.
- Reports and raw child output remain local; child stdout/stderr is not copied into this aggregate report.

| Drill | Result | Duration (ms) |
|---|---|---:|
EOF
for index in "${!names[@]}"; do
    result=PASS
    if [[ "${statuses[$index]}" != 0 ]]; then
        result=FAIL
    fi
    printf '| %s | %s | %s |\n' "${names[$index]}" "$result" "${durations[$index]}" >> "$report_file"
done

if (( failed != 0 )); then
    if ! python3 - "$diagnostics_dir" <<'PY'
import os
import sys

path = os.path.abspath(sys.argv[1])
sys.exit(0 if os.path.realpath(path) == path else 1)
PY
    then
        echo "Recovery diagnostics path must not contain symlink components: $diagnostics_dir" >&2
        exit 2
    fi
    if [[ -e "$diagnostics_dir" || -L "$diagnostics_dir" ]]; then
        echo "Recovery diagnostics path already exists; refusing to follow it: $diagnostics_dir" >&2
        exit 2
    fi
    mkdir -m 700 -- "$diagnostics_dir"
    for index in "${!statuses[@]}"; do
        [[ "${statuses[$index]}" != 0 ]] || continue
        input_file="$work_dir/${names[$index]}.log"
        output_file="$diagnostics_dir/${names[$index]}.log"
        python3 - "$input_file" "$output_file" <<'PY'
import re
import sys
from collections import deque

source, destination = sys.argv[1:]
secret_assignment = re.compile(
    r'(?i)(\b[a-z0-9_.-]*(?:password|passwd|secret|token|api[_-]?key|authorization|'
    r'connection(?:string)?|private[_-]?key)\b["\']?\s*[=:]\s*["\']?)'
    r'([^"\'\s,;]+)'
)
bearer = re.compile(r'(?i)\b(Bearer)\s+[A-Za-z0-9._~+/-]+=*')
url_password = re.compile(r'(?i)([a-z][a-z0-9+.-]*://[^:/\s]+:)[^@/\s]+(@)')
lines = deque(maxlen=200)
with open(source, 'rb') as stream:
    while True:
        raw_line = stream.readline(16385)
        if not raw_line:
            break
        if len(raw_line) > 16384:
            while raw_line and not raw_line.endswith(b'\n'):
                raw_line = stream.readline(16385)
            lines.append('[REDACTED: oversized diagnostic line omitted]\n')
            continue

        line = raw_line.decode('utf-8', errors='replace')
        line = secret_assignment.sub(r'\1[REDACTED]', line)
        line = bearer.sub(r'\1 [REDACTED]', line)
        line = url_password.sub(r'\1[REDACTED]\2', line)
        lines.append(line)
payload = ''.join(lines).encode('utf-8', errors='replace')[-16384:]
with open(destination, 'wb') as stream:
    stream.write(payload)
PY
        chmod 600 "$output_file"
    done
    printf '\n- Overall result: **FAIL**; sanitized diagnostics (last 200 lines, capped at 16 KiB per failed drill) are preserved in `%s`. Review before sharing.\n' "$diagnostics_dir" >> "$report_file"
    chmod 600 "$report_file"
    echo "Full local recovery drill failed; redacted report: $report_file" >&2
    echo "Sanitized failure diagnostics: $diagnostics_dir" >&2
    exit 1
fi

cat >> "$report_file" <<'EOF'

- Overall result: **PASS**.
- Interpretation: local ledger, full-state manifest, persistence restart/idempotency, and ephemeral backup/restore gates passed together.
- Limitation: this does not prove production RPO/RTO, authenticated exchange recovery, or reconstruction of data not present in the local fixtures.
EOF
chmod 600 "$report_file"
echo "Full local recovery drill passed; redacted report: $report_file"
