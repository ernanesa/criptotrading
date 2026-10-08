#!/usr/bin/env bash
set -Eeuo pipefail

# Local deterministic OOS/walk-forward sensitivity evidence. It uses only the
# versioned synthetic dataset and never loads credentials or contacts an
# exchange. Real-market OOS remains a separate readiness gate.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo "Refusing OOS sensitivity run: Binance credentials are present; unset them first." >&2
    exit 2
fi

report_dir="$repo_root/artifacts/oos"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/sensitivity-$timestamp-$$.md"
test_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-oos-sensitivity.XXXXXX")"
cleanup() {
    rm -f -- "$test_log"
}
trap cleanup EXIT

test_project="$repo_root/tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj"
set +e
env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" test "$test_project" \
    --no-restore \
    --configuration Release \
    --minimum-expected-tests 1 \
    -- --filter-trait 'Category=OosSensitivity' > >(tee "$test_log") 2>&1
test_status=$?
set -e

summary="$(rg -A5 '^Test run summary:' "$test_log" | tr '\n' ' ' | sed -E 's/[[:space:]]+/ /g' || true)"
if [[ -z "$summary" ]]; then
    summary="MTP exit code: $test_status; minimum expected tests: 1."
fi

result="FAIL"
if (( test_status == 0 )); then
    result="PASS"
fi

{
    echo "# OOS sensitivity report"
    echo
    printf '%s\n' "- Result: **$result**"
    printf '%s\n' "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
    printf '%s\n' "- Dataset: \`synthetic-btcusdt-oos/1.0.0\`"
    printf '%s\n' "- Windows: \`6 chronological windows (60 train / 20 OOS / 20 step)\`"
    printf '%s\n' "- Cases: \`baseline, faster-10-30, cost-stress\`"
    echo "- Network access: none"
    echo "- Credentials: none loaded or accepted"
    echo "- Winner selection: none; every preregistered case is retained"
    printf '%s\n' "- Test summary: \`$summary\`"
    echo
    echo "## Evidence boundary"
    echo
    echo "This report validates deterministic pipeline execution, chronological"
    echo "splits, case coverage and reproducibility on synthetic data only. It is"
    echo "not evidence of real-market OOS edge, execution sensitivity, or promotion."
} > "$report_file"

echo "OOS sensitivity report: $report_file"
if [[ "$result" != PASS ]]; then
    echo "OOS sensitivity failed closed; inspect the test output above and the report." >&2
    exit 1
fi

echo "OOS sensitivity passed: all preregistered synthetic cases were evaluated."
