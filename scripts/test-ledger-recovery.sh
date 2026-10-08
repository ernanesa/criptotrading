#!/usr/bin/env bash
set -Eeuo pipefail

# Deterministic local ledger reconstruction evidence. This runner is memory-only
# and intentionally has no exchange, network, credential, or database access.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing ledger recovery drill: Binance credentials are present; unset them first." >&2
    exit 2
fi

report_dir="$repo_root/artifacts/recovery"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/ledger-recovery-$timestamp-$$.md"
test_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-ledger-recovery.XXXXXX")"
cleanup() {
    rm -f -- "$test_log"
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Domain.Tests/CryptoTrading.Domain.Tests.csproj"
started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
if "$dotnet_command" test "$test_project" \
    --no-restore \
    --configuration Release \
    --minimum-expected-tests 1 \
    -- --filter-trait 'Category=RecoveryDrill' >"$test_log" 2>&1; then
    test_status=0
else
    test_status=$?
fi
tee /dev/stderr <"$test_log" >/dev/null
finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

summary="$(rg -A5 '^Test run summary:' "$test_log" | tr '\n' ' ' | sed -E 's/[[:space:]]+/ /g' || true)"
if [[ -z "$summary" ]]; then
    summary="MTP exit code: $test_status; minimum expected tests: 1."
fi

result="FAIL"
if (( test_status == 0 )); then
    result="PASS"
fi

{
    echo "# Ledger reconstruction recovery report"
    echo
    echo "- Result: **$result**"
    printf '%s\n' "- Started (UTC): \`$started_at\`"
    printf '%s\n' "- Finished (UTC): \`$finished_at\`"
    printf '%s\n' "- Harness: \`CryptoTrading.Domain.Tests\`"
    printf '%s\n' "- Filter: \`Category=RecoveryDrill\`"
    echo "- Execution mode: deterministic in-memory journal reconstruction"
    echo "- Network access: none"
    echo "- Credentials: none loaded or accepted"
    echo "- Source journal mutation: none"
    printf '%s\n' "- Test summary: \`$summary\`"
    echo
    echo "## Evidence boundary"
    echo
    echo "This artifact proves only deterministic reconstruction of the in-memory"
    echo "ledger projection. It does not prove PostgreSQL RPO/RTO, backup restore,"
    echo "external alert delivery, or promotion readiness."
} >"$report_file"

echo "Ledger recovery report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "Ledger recovery drill failed closed; inspect the test output above and the report." >&2
    exit 1
fi

echo "Ledger recovery drill passed: deterministic journal reconstruction completed."
