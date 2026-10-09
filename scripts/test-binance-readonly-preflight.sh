#!/usr/bin/env bash
set -Eeuo pipefail

# Offline safety harness for the authenticated Testnet probe. It deliberately
# runs from a directory without .env and removes both credential variables, so
# success means the probe refused before constructing an authenticated client.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_dir="$repo_root/artifacts/binance"
if [[ "${PREFLIGHT_TEST_MODE:-}" == "1" ]]; then
    report_dir="${PREFLIGHT_TEST_REPORT_DIR:-$report_dir}"
fi
if ! python3 - "$report_dir" <<'PY'
import os
import sys

path = os.path.abspath(sys.argv[1])
sys.exit(0 if os.path.realpath(path) == path else 1)
PY
then
    echo 'Refusing to run: preflight report directory contains a symlink component.' >&2
    exit 2
fi
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-readonly-preflight.XXXXXX")"
probe_log="$temp_dir/probe.log"
cleanup() {
    rm -rf -- "$temp_dir"
}
trap cleanup EXIT

dotnet_command="dotnet"
if [[ "${PREFLIGHT_TEST_MODE:-}" == "1" ]]; then
    dotnet_command="${PREFLIGHT_TEST_EXECUTABLE:?test mode requires PREFLIGHT_TEST_EXECUTABLE}"
elif [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_command="$HOME/.dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo 'Refusing to run: preflight must never receive Binance credentials.' >&2
    exit 2
fi

if [[ "${WORKER_NO_TRADE:-true}" != "true" || "${WORKER_EXECUTION_MODE:-PAPER}" != "PAPER" ]]; then
    echo 'Refusing to run: WORKER_NO_TRADE=true and WORKER_EXECUTION_MODE=PAPER are required.' >&2
    exit 2
fi

set +e
(
    cd -- "$temp_dir"
    env -u BINANCE_API_KEY -u BINANCE_API_SECRET -u BINANCE_API_KEY_FILE -u BINANCE_API_SECRET_FILE \
        TRADING_ENVIRONMENT=TESTNET \
        BINANCE_REST_BASE_URL=https://testnet.binance.vision/api \
        BINANCE_WITHDRAWALS_ENABLED=false \
        WORKER_NO_TRADE=true \
        WORKER_EXECUTION_MODE=PAPER \
        "$dotnet_command" run \
            --project "$repo_root/tools/CryptoTrading.BinanceReadOnlyProbe/CryptoTrading.BinanceReadOnlyProbe.csproj" \
            --no-restore \
            --configuration Release
) >"$probe_log" 2>&1
probe_status=$?
set -e

if (( probe_status != 2 )) || ! grep -q 'Binance credentials must be supplied through local values or protected credential files' "$probe_log"; then
    diagnostics_dir="$(mktemp -d "$report_dir/.readonly-preflight-diagnostics.XXXXXX")"
    chmod 700 "$diagnostics_dir"
    diagnostics_file="$diagnostics_dir/probe.log"
    python3 "$repo_root/scripts/redact-diagnostic-log.py" "$probe_log" "$diagnostics_file"
    umask 077
    report_file="$(mktemp "$report_dir/read-only-preflight-failure-$timestamp-$$.XXXXXX.md")"
    {
        echo '# Binance read-only offline preflight — failure'
        echo
        echo "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
        echo '- Credentials: absent by construction'
        echo "- Probe exit code: \`$probe_status\`"
        echo "- Sanitized bounded diagnostics: \`$diagnostics_file\`"
    } >"$report_file"
    chmod 600 "$report_file"
    echo "Refusing to pass: probe did not fail closed on missing credentials (exit $probe_status)." >&2
    echo "Sanitized diagnostics: $diagnostics_file" >&2
    exit 1
fi

if grep -Eiq '(signature=|X-MBX-APIKEY|secret=)' "$probe_log"; then
    echo 'Refusing to pass: probe output contains sensitive request material.' >&2
    exit 1
fi

umask 077
report_file="$(mktemp "$report_dir/read-only-preflight-$timestamp-$$.XXXXXX.md")"
{
    echo '# Binance read-only offline preflight'
    echo
    echo '- Result: **PASS**'
    echo "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
    echo '- Credentials: absent by construction'
    echo '- Working directory: temporary directory without `.env`'
    echo '- Safety mode: `WORKER_NO_TRADE=true`, `WORKER_EXECUTION_MODE=PAPER`'
    echo '- Expected behavior: authenticated probe refused before network client creation'
    echo '- Probe exit code: `2`'
} >"$report_file"
chmod 600 "$report_file"
[[ "$(stat -c '%a' -- "$report_file")" == "600" ]] || {
    echo 'Read-only offline preflight report permissions could not be restricted to mode 600.' >&2
    exit 1
}

echo "Read-only offline preflight report: $report_file"
