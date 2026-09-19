#!/usr/bin/env bash
set -Eeuo pipefail

# Composite local recovery gate. Each child drill owns its own ephemeral
# resources; this runner only aggregates exit status and bounded metadata.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="$repo_root/artifacts/recovery"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/full-recovery-$run_stamp-$$.md"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-full-recovery.XXXXXX")"
umask 077
mkdir -p -- "$report_dir"

cleanup() { rm -rf -- "$work_dir"; }
trap cleanup EXIT

declare -a names=(ledger-recovery postgres-integration postgres-backup-restore)
declare -a commands=(
    "scripts/test-ledger-recovery.sh"
    "scripts/test-postgres-integration.sh"
    "scripts/test-postgres-backup-restore.sh"
)
declare -a statuses=()
declare -a durations=()

for index in "${!names[@]}"; do
    started="$(date +%s%N)"
    output_file="$work_dir/${names[$index]}.log"
    status=0
    if (cd "$repo_root" && "${repo_root}/${commands[$index]}" >"$output_file" 2>&1); then
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
- Composite gate: ledger reconstruction, PostgreSQL integration/restart, and ephemeral backup/restore.
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
    printf '\n- Overall result: **FAIL**; inspect child command output from the failing drill while it is still available in the invoking session.\n' >> "$report_file"
    chmod 600 "$report_file"
    echo "Full local recovery drill failed; redacted report: $report_file" >&2
    exit 1
fi

cat >> "$report_file" <<'EOF'

- Overall result: **PASS**.
- Interpretation: local ledger, persistence restart/idempotency, and ephemeral backup/restore gates passed together.
- Limitation: this does not prove production RPO/RTO, authenticated exchange recovery, or reconstruction of data not present in the local fixtures.
EOF
chmod 600 "$report_file"
echo "Full local recovery drill passed; redacted report: $report_file"
