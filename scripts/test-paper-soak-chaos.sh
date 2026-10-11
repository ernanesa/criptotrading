#!/usr/bin/env bash
set -Eeuo pipefail

# Deterministic local PAPER/NO_TRADE chaos evidence. This runner refuses
# credentials and invokes only the isolated Worker test harness; it has no
# Binance client, network path, database, or venue order capability.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing PAPER chaos/soak: Binance credentials are present; unset them first." >&2
    exit 2
fi

soak_runs="${PAPER_CHAOS_RUNS:-3}"
if [[ ! "$soak_runs" =~ ^[1-9][0-9]*$ ]] || (( soak_runs > 100 )); then
    echo "Invalid PAPER_CHAOS_RUNS='$soak_runs'; expected an integer from 1 to 100." >&2
    exit 2
fi

umask 077
report_dir="$repo_root/artifacts/chaos"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/paper-soak-chaos-$timestamp-$$.md"
cleanup() {
    if [[ -n "${run_log:-}" ]]; then
        rm -f -- "$run_log"
    fi
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Worker.Tests/CryptoTrading.Worker.Tests.csproj"
test_source="$repo_root/tests/CryptoTrading.Worker.Tests/PaperSoakChaosTests.cs"
base_commit="$(git -C "$repo_root" rev-parse HEAD)"
worktree_status="$(git -C "$repo_root" -c core.fsmonitor=false status --porcelain=v1)"
worktree_state="clean"
if [[ -n "$worktree_status" ]]; then
    worktree_state="dirty"
fi
worktree_status_sha256="$(printf '%s' "$worktree_status" | sha256sum | awk '{print $1}')"
runner_sha256="$(sha256sum "$repo_root/scripts/test-paper-soak-chaos.sh" | awk '{print $1}')"
scenario_sha256="$(sha256sum "$test_source" | awk '{print $1}')"
test_command=(
    env -u BINANCE_API_KEY -u BINANCE_API_SECRET
    "$dotnet_command" test "$test_project"
    --no-restore
    --configuration Release
    --minimum-expected-tests 1
    -- --filter-trait 'Category=PaperChaos'
)

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
started_epoch_ms="$(date +%s%3N)"
all_passed=1
declare -a run_summaries=()
for ((run_index = 1; run_index <= soak_runs; run_index++)); do
    run_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-paper-chaos-run.XXXXXX")"
    run_started_epoch_ms="$(date +%s%3N)"
    if "${test_command[@]}" >"$run_log" 2>&1; then
        run_status=0
        run_result=PASS
    else
        run_status=$?
        run_result=FAIL
        run_stop_reason="test-process-exit-$run_status"
        all_passed=0
    fi
    if (( run_status == 0 )); then
        run_stop_reason="none; repetition completed"
    fi
    run_finished_epoch_ms="$(date +%s%3N)"
    run_duration_ms=$((run_finished_epoch_ms - run_started_epoch_ms))

    # Raw test output remains ephemeral and is never copied into the report.
    run_summaries+=("Repetition $run_index/$soak_runs: result=$run_result; duration_ms=$run_duration_ms; stop_reason=$run_stop_reason; MTP_exit_code=$run_status")
    rm -f -- "$run_log"
done
finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
finished_epoch_ms="$(date +%s%3N)"
total_duration_ms=$((finished_epoch_ms - started_epoch_ms))

result=FAIL
if (( all_passed == 1 )); then
    result=PASS
fi

{
    echo "# PAPER chaos/soak report"
    echo
    printf '%s\n' "- Result: **$result**"
    printf '%s\n' "- Started (UTC): \`$started_at\`"
    printf '%s\n' "- Finished (UTC): \`$finished_at\`"
    printf '%s\n' "- Duration (ms): \`$total_duration_ms\`"
    printf '%s\n' "- Base commit: \`$base_commit\`"
    printf '%s\n' "- Worktree state: \`$worktree_state\`"
    printf '%s\n' "- Worktree status SHA-256: \`$worktree_status_sha256\`"
    printf '%s\n' "- Runner SHA-256: \`$runner_sha256\`"
    printf '%s\n' "- Scenario ID: \`PAPER-CHAOS-001\`"
    printf '%s\n' "- Scenario source SHA-256: \`$scenario_sha256\`"
    printf '%s\n' "- Harness: \`CryptoTrading.Worker.Tests\`"
    printf '%s\n' "- Filter: \`Category=PaperChaos\`"
    printf '%s\n' "- Repetitions: \`$soak_runs\`"
    printf '%s\n' "- Threshold acceptance: none; L26 is pending"
    echo "- Execution boundary: local deterministic PAPER harness with runtime NO_TRADE outside the test fixture"
    echo "- Network and database access: none"
    echo "- Credentials: refused before execution; none loaded"
    echo "- Venue submit/cancel calls: zero"
    echo "- Durable report mode: 0600"
    echo "- Run results:"
    for run_summary in "${run_summaries[@]}"; do
        printf '%s\n' "  - \`$run_summary\`"
    done
    if (( all_passed == 1 )); then
        echo "- Stop reason: all configured repetitions completed"
    else
        echo "- Stop reason: one or more repetitions failed; configured repetitions were completed"
    fi
    echo
    echo "## Evidence boundary"
    echo
    echo "This artifact proves only the deterministic in-memory scenarios selected by"
    echo "the filtered test: duplicate producer delivery, transient pre-acceptance"
    echo "failure, restart after simulated paper acceptance, lease recovery, and"
    echo "idempotent simulated-paper replay. It is not evidence of Binance/Testnet"
    echo "submission, reconciliation, storage durability, or Canary/Live readiness."
} >"$report_file"
chmod 600 "$report_file"

echo "PAPER chaos/soak report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "PAPER chaos/soak failed closed; raw test output was withheld from the report." >&2
    exit 1
fi

echo "PAPER chaos/soak passed: deterministic local chaos scenarios completed."
