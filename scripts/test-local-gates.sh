#!/usr/bin/env bash
set -Eeuo pipefail

# Runs independent, offline/local quality gates concurrently. The aggregate
# report contains only status and duration; child output stays in a private
# temporary directory and is never copied into the report.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="$repo_root/artifacts/quality"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/local-gates-$run_stamp-$$.md"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-local-gates.XXXXXX")"
umask 077
mkdir -p -- "$report_dir"

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

api_port=$((58080 + ($$ % 1000)))
names=(
    readiness-validator
    traceability-validator
    secret-storage
    secret-initializer
    binance-readonly-preflight
    api-smoke
    operational-control
    shadow-operational-gate
    oos-sensitivity
    paper-soak-chaos
    ledger-recovery
)
commands=(
    scripts/test-readiness-validator.sh
    scripts/test-traceability-validator.sh
    scripts/test-secret-storage-preflight.sh
    scripts/test-secret-initializer.sh
    scripts/test-binance-readonly-preflight.sh
    scripts/test-api-smoke.sh
    scripts/test-operational-control.sh
    scripts/test-shadow-operational-gate.sh
    scripts/test-oos-sensitivity.sh
    scripts/test-paper-soak-chaos.sh
    scripts/test-ledger-recovery.sh
)

declare -a pids=()
declare -a started=()
# PaperSoak invokes a project build internally; keep only this build-sensitive
# gate serialized to avoid MSBuild output/intermediate-file contention.
paper_index=9
started[$paper_index]="$(date +%s%N)"
paper_log="$work_dir/${names[$paper_index]}.log"
paper_status=0
(cd "$repo_root" && "${repo_root}/${commands[$paper_index]}" >"$paper_log" 2>&1) || paper_status=$?
paper_finished="$(date +%s%N)"

for index in "${!names[@]}"; do
    if (( index == paper_index )); then
        pids[$index]=0
        continue
    fi
    started[$index]="$(date +%s%N)"
    log_file="$work_dir/${names[$index]}.log"
    if [[ "${names[$index]}" == "api-smoke" ]]; then
        (cd "$repo_root" && API_TEST_PORT="$api_port" "${repo_root}/${commands[$index]}" >"$log_file" 2>&1) &
    else
        (cd "$repo_root" && "${repo_root}/${commands[$index]}" >"$log_file" 2>&1) &
    fi
    pids[$index]=$!
done

declare -a statuses=()
declare -a durations=()
failed=0
for index in "${!names[@]}"; do
    if (( index == paper_index )); then
        statuses[$index]="$paper_status"
        durations[$index]="$(( (paper_finished - started[$index]) / 1000000 ))"
        [[ "$paper_status" == 0 ]] || failed=1
        continue
    fi
    status=0
    wait "${pids[$index]}" || status=$?
    finished="$(date +%s%N)"
    statuses[$index]="$status"
    durations[$index]="$(( (finished - started[$index]) / 1000000 ))"
    [[ "$status" == 0 ]] || failed=1
done

cat >"$report_file" <<EOF
# Local quality gates — $run_stamp

- Scope: independent local/offline gates executed concurrently.
- No Binance credentials, authenticated requests, order endpoints or capital were used.
- Child output remains private and is not copied into this report.

| Gate | Result | Duration (ms) |
|---|---|---:|
EOF
for index in "${!names[@]}"; do
    result=PASS
    [[ "${statuses[$index]}" == 0 ]] || result=FAIL
    printf '| %s | %s | %s |\n' "${names[$index]}" "$result" "${durations[$index]}" >>"$report_file"
done

if (( failed != 0 )); then
    printf '\n- Overall result: **FAIL**. Inspect the invoking session and rerun the failing gate individually.\n' >>"$report_file"
    chmod 600 "$report_file"
    echo "Local quality gates failed; redacted report: $report_file" >&2
    exit 1
fi

printf '\n- Overall result: **PASS**.\n' >>"$report_file"
chmod 600 "$report_file"
echo "Local quality gates passed; redacted report: $report_file"
