#!/usr/bin/env bash
set -Eeuo pipefail

# Deterministic local Shadow soak evidence. This runner intentionally has no
# exchange client, network access, credential loading, or order capability.
# The test itself owns the strategy/Paper fixtures and is selected explicitly
# so a green unrelated test suite cannot masquerade as Shadow evidence.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

# Refuse to run when credentials are present rather than silently normalizing
# the environment. This makes an accidental authenticated execution fail
# closed and leaves the caller's environment unchanged.
if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing Shadow soak: Binance credentials are present; unset them first." >&2
    exit 2
fi

soak_runs="${SHADOW_SOAK_RUNS:-3}"
if [[ ! "$soak_runs" =~ ^[1-9][0-9]*$ ]] || (( soak_runs > 100 )); then
    echo "Invalid SHADOW_SOAK_RUNS='$soak_runs'; expected an integer from 1 to 100." >&2
    exit 2
fi

umask 077
report_dir="$repo_root/artifacts/shadow"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/shadow-soak-$timestamp-$$.md"
cleanup() {
    if [[ -n "${run_log:-}" ]]; then
        rm -f -- "$run_log"
    fi
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj"
test_source="$repo_root/tests/CryptoTrading.Backtesting.Tests/ShadowSoakTests.cs"
base_commit="$(git -C "$repo_root" rev-parse HEAD)"
worktree_status="$(git -C "$repo_root" -c core.fsmonitor=false status --porcelain=v1)"
worktree_state="clean"
if [[ -n "$worktree_status" ]]; then
    worktree_state="dirty"
fi
worktree_status_sha256="$(printf '%s' "$worktree_status" | sha256sum | awk '{print $1}')"
runner_sha256="$(sha256sum "$repo_root/scripts/test-shadow-soak.sh" | awk '{print $1}')"
scenario_sha256="$(sha256sum "$test_source" | awk '{print $1}')"
test_command=(
    env -u BINANCE_API_KEY -u BINANCE_API_SECRET
    "$dotnet_command" test "$test_project"
    --no-restore
    --configuration Release
    --minimum-expected-tests 1
    -- --filter-trait 'Category=ShadowSoak'
)

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
started_epoch_ms="$(date +%s%3N)"
all_passed=1
declare -a run_summaries=()
for ((run_index = 1; run_index <= soak_runs; run_index++)); do
    run_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-shadow-soak-run.XXXXXX")"
    run_started_epoch_ms="$(date +%s%3N)"
    if "${test_command[@]}" >"$run_log" 2>&1; then
        run_status=0
    else
        run_status=$?
    fi
    if (( run_status == 0 )); then
        run_result=PASS
        run_stop_reason="none; repetition completed"
    else
        run_result=FAIL
        run_stop_reason="test-process-exit-$run_status"
    fi
    run_finished_epoch_ms="$(date +%s%3N)"
    run_duration_ms=$((run_finished_epoch_ms - run_started_epoch_ms))
    # Raw test output remains ephemeral and is never copied into the report.
    run_summaries+=("Repetition $run_index/$soak_runs: result=$run_result; duration_ms=$run_duration_ms; stop_reason=$run_stop_reason; MTP_exit_code=$run_status")
    if (( run_status != 0 )); then
        all_passed=0
    fi
    rm -f -- "$run_log"
done
finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
finished_epoch_ms="$(date +%s%3N)"
total_duration_ms=$((finished_epoch_ms - started_epoch_ms))

result="FAIL"
if (( all_passed == 1 )); then
    result="PASS"
fi

# The report contains only deterministic run metadata and test summary. Never
# copy process output wholesale: this keeps future fixture changes from
# accidentally turning a diagnostic value into an evidence leak.
{
    echo "# Shadow soak report"
    echo
    echo "- Result: **$result**"
    printf '%s\n' "- Started (UTC): \`$started_at\`"
    printf '%s\n' "- Finished (UTC): \`$finished_at\`"
    printf '%s\n' "- Duration (ms): \`$total_duration_ms\`"
    printf '%s\n' "- Base commit: \`$base_commit\`"
    printf '%s\n' "- Worktree state: \`$worktree_state\`"
    printf '%s\n' "- Worktree status SHA-256: \`$worktree_status_sha256\`"
    printf '%s\n' "- Runner SHA-256: \`$runner_sha256\`"
    printf '%s\n' "- Scenario ID: \`SHADOW-SOAK-001\`"
    printf '%s\n' "- Scenario source SHA-256: \`$scenario_sha256\`"
    printf '%s\n' "- Harness: \`CryptoTrading.Backtesting.Tests\`"
    printf '%s\n' "- Filter: \`Category=ShadowSoak\`"
    printf '%s\n' "- Repetitions: \`$soak_runs\`"
    echo "- Threshold acceptance: none; L26 is pending"
    echo "- Execution mode: local deterministic Shadow/Paper comparison"
    echo "- Network access: none"
    echo "- Credentials: refused before execution; none loaded"
    echo "- Exchange submit/cancel capability: none"
    echo "- Durable report mode: 0600"
    echo "- Test results:"
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
    echo "This artifact proves only the local deterministic soak represented by the"
    echo "filtered test. It does not prove authenticated Testnet reconciliation,"
    echo "real-market OOS performance, alert delivery, or Canary/Live readiness."
} > "$report_file"
chmod 600 "$report_file"

echo "Shadow soak report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "Shadow soak failed closed; raw test output was withheld from the report." >&2
    exit 1
fi

echo "Shadow soak passed: deterministic local strategy/Paper comparison completed."
