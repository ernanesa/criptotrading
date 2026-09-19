#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="$repo_root/artifacts/binance"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/read-only-probe-$timestamp-$$.md"
probe_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-binance-probe.XXXXXX")"
cleanup() {
    rm -f -- "$probe_log"
}
trap cleanup EXIT

dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -z "${BINANCE_API_KEY:-}" || -z "${BINANCE_API_SECRET:-}" ]]; then
    echo 'Refusing to run: provide fresh Testnet credentials through the environment only.' >&2
    exit 2
fi
if [[ "${TRADING_ENVIRONMENT:-TESTNET}" != "TESTNET" ]]; then
    echo 'Refusing to run: TRADING_ENVIRONMENT must be TESTNET.' >&2
    exit 2
fi

export BINANCE_REST_BASE_URL="${BINANCE_REST_BASE_URL:-https://testnet.binance.vision/api}"
case "$BINANCE_REST_BASE_URL" in
    https://testnet.binance.vision/api|https://testnet.binance.vision/api/) ;;
    *) echo 'Refusing to run: BINANCE_REST_BASE_URL must be the exact Spot Testnet API endpoint.' >&2; exit 2 ;;
esac

export BINANCE_WITHDRAWALS_ENABLED="${BINANCE_WITHDRAWALS_ENABLED:-false}"
case "$BINANCE_WITHDRAWALS_ENABLED" in
    false|FALSE) ;;
    *) echo 'Refusing to run: BINANCE_WITHDRAWALS_ENABLED must be false.' >&2; exit 2 ;;
esac

set +e
"$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.BinanceReadOnlyProbe/CryptoTrading.BinanceReadOnlyProbe.csproj" \
    --no-restore \
    --configuration Release >"$probe_log" 2>&1
probe_status=$?
set -e

result="FAIL"
if (( probe_status == 0 )); then
    result="PASS"
fi

{
    echo "# Binance Spot Testnet read-only probe"
    echo
    echo "- Result: **$result**"
    echo "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
    echo "- Environment: \`TESTNET\`"
    echo "- Endpoint policy: exact HTTPS Spot Testnet `/api` endpoint"
    echo "- Credentials: supplied locally, never written to this report"
    echo "- Exchange mutation: not available (account/open-orders/trades only)"
    echo "- Probe exit code: \`$probe_status\`"
    echo
    echo "The process output is intentionally not copied here. Inspect it only in"
    echo "a controlled local terminal if troubleshooting is required; never paste"
    echo "credentials, signed URLs, headers, or raw exchange responses into logs."
} > "$report_file"

echo "Read-only probe report: $report_file"
if (( probe_status != 0 )); then
    echo "Read-only probe failed; raw output was withheld from the report." >&2
    exit "$probe_status"
fi

echo 'Binance Spot Testnet authenticated read-only probe completed; report redacted.'
