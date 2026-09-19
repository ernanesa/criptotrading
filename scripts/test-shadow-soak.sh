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

report_dir="$repo_root/artifacts/shadow"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/shadow-soak-$timestamp-$$.md"
test_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-shadow-soak.XXXXXX")"
cleanup() {
    rm -f -- "$test_log"
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj"
test_command=(
    env -u BINANCE_API_KEY -u BINANCE_API_SECRET
    "$dotnet_command" test "$test_project"
    --no-restore
    --configuration Release
    --filter 'Category=ShadowSoak'
    --logger 'console;verbosity=minimal'
)

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
set +e
"${test_command[@]}" > >(tee "$test_log") 2>&1
test_status=$?
set -e
finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

summary="$(rg -o 'Passed!.*' "$test_log" | tail -n 1 || true)"
if [[ -z "$summary" ]]; then
    summary="No test summary was emitted."
fi

result="FAIL"
if (( test_status == 0 )) \
    && [[ "$summary" =~ Passed! ]] \
    && [[ "$summary" =~ Failed:[[:space:]]+0 ]] \
    && [[ "$summary" =~ Total:[[:space:]]+[1-9][0-9]* ]]; then
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
    printf '%s\n' "- Harness: \`CryptoTrading.Backtesting.Tests\`"
    printf '%s\n' "- Filter: \`Category=ShadowSoak\`"
    echo "- Execution mode: local deterministic Shadow/Paper comparison"
    echo "- Network access: none"
    echo "- Credentials: none loaded or accepted"
    echo "- Exchange submit/cancel capability: none"
    printf '%s\n' "- Test summary: \`$summary\`"
    echo
    echo "## Evidence boundary"
    echo
    echo "This artifact proves only the local deterministic soak represented by the"
    echo "filtered test. It does not prove authenticated Testnet reconciliation,"
    echo "real-market OOS performance, alert delivery, or Canary/Live readiness."
} > "$report_file"

echo "Shadow soak report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "Shadow soak failed closed; inspect the test output above and the report." >&2
    exit 1
fi

echo "Shadow soak passed: deterministic local strategy/Paper comparison completed."
