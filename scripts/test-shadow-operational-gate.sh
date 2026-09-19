#!/usr/bin/env bash
set -Eeuo pipefail

# Local-only Shadow quality-gate evidence. This evaluates bounded metrics and
# explicit thresholds; it has no exchange, network, credential, or order path.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing Shadow operational gate: Binance credentials are present; unset them first." >&2
    exit 2
fi

report_dir="$repo_root/artifacts/shadow"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/operational-gate-$timestamp-$$.md"
test_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-shadow-gate.XXXXXX")"
cleanup() {
    rm -f -- "$test_log"
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Shadow.Tests/CryptoTrading.Shadow.Tests.csproj"
set +e
env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" test "$test_project" \
    --no-restore \
    --configuration Release \
    --filter 'Category=ShadowOperational' \
    --logger 'console;verbosity=minimal' > >(tee "$test_log") 2>&1
test_status=$?
set -e

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

{
    echo "# Shadow operational gate report"
    echo
    printf '%s\n' "- Result: **$result**"
    printf '%s\n' "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
    echo "- Scope: local deterministic metric/threshold evaluation"
    echo "- Metrics: divergence, stale, recovery, intervention-rate, duration"
    echo "- Network access: none"
    echo "- Credentials: none loaded or accepted"
    echo "- Exchange submit/cancel capability: none"
    printf '%s\n' "- Test summary: \`$summary\`"
    echo
    echo "## Evidence boundary"
    echo
    echo "A PASS proves only that explicit local thresholds are evaluated fail-closed."
    echo "It does not prove a real-market Shadow period, intervention behavior under"
    echo "production load, alert delivery, or Canary/Live readiness."
} > "$report_file"

echo "Shadow operational gate report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "Shadow operational gate failed closed; inspect the test output above and the report." >&2
    exit 1
fi

echo "Shadow operational gate passed: all quality-gate scenarios validated."
