#!/usr/bin/env bash
set -Eeuo pipefail

# Local SafeMode/KillSwitch drill. It has no exchange, network, credential, or
# order capability; it validates only the independent risk-control boundary.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing operational-control drill: Binance credentials are present; unset them first." >&2
    exit 2
fi

report_dir="$repo_root/artifacts/operations"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/operational-control-$timestamp-$$.md"
test_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-operational-control.XXXXXX")"
cleanup() {
    rm -f -- "$test_log"
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Domain.Tests/CryptoTrading.Domain.Tests.csproj"
started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
if "$dotnet_command" test "$test_project" \
    --no-restore \
    --configuration Release \
    --filter 'Category=OperationalControl' \
    --logger 'console;verbosity=minimal' >"$test_log" 2>&1; then
    test_status=0
else
    test_status=$?
fi
tee /dev/stderr <"$test_log" >/dev/null
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

{
    echo "# Operational control drill report"
    echo
    echo "- Result: **$result**"
    printf '%s\n' "- Started (UTC): \`$started_at\`"
    printf '%s\n' "- Finished (UTC): \`$finished_at\`"
    printf '%s\n' "- Harness: \`CryptoTrading.Domain.Tests\`"
    printf '%s\n' "- Filter: \`Category=OperationalControl\`"
    echo "- Controls: SafeMode, KillSwitch, reset authorization, idempotency"
    echo "- Network access: none"
    echo "- Credentials: none loaded or accepted"
    echo "- Order capability: none"
    printf '%s\n' "- Test summary: \`$summary\`"
    echo
    echo "## Evidence boundary"
    echo
    echo "This artifact proves only local risk-control invariants and does not"
    echo "prove exchange cancellation, external alert delivery, or promotion readiness."
} >"$report_file"

echo "Operational-control report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "Operational-control drill failed closed; inspect the test output above and the report." >&2
    exit 1
fi

echo "Operational-control drill passed: SafeMode/KillSwitch invariants validated."
