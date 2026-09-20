#!/usr/bin/env bash
set -Eeuo pipefail

# Starts the real local API with synthetic configuration only. No exchange,
# database, credential file or mutating endpoint is used by this smoke test.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
port="${API_TEST_PORT:-58081}"
[[ "$port" =~ ^[0-9]+$ ]] || { echo 'API_TEST_PORT must be numeric.' >&2; exit 2; }

dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_command="$HOME/.dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

log_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-api-smoke.XXXXXX")"
cleanup() {
    if [[ -n "${api_pid:-}" ]]; then
        kill "$api_pid" >/dev/null 2>&1 || true
        wait "$api_pid" >/dev/null 2>&1 || true
    fi
    rm -f -- "$log_file"
}
trap cleanup EXIT

key_variable=BINANCE_API_KEY
secret_variable=BINANCE_API_SECRET

# Build before exporting even synthetic credential values: Directory.Build.targets
# intentionally rejects credential-shaped environment variables during MSBuild.
"$dotnet_command" build \
    "$repo_root/src/CryptoTrading.Api/CryptoTrading.Api.csproj" \
    --no-restore --configuration Release >/dev/null

export TRADING_ENVIRONMENT=TESTNET
export BINANCE_REST_BASE_URL=https://testnet.binance.vision/api
export BINANCE_WS_BASE_URL=wss://testnet.binance.vision/ws
export BINANCE_WITHDRAWALS_ENABLED=false
export BINANCE_ALLOWED_SYMBOLS=BTCUSDT
export "$key_variable=synthetic-$(date +%s%N)"
export "$secret_variable=synthetic-$(date +%s%N)"

"$dotnet_command" run \
    --project "$repo_root/src/CryptoTrading.Api/CryptoTrading.Api.csproj" \
    --no-build --no-restore --configuration Release --no-launch-profile \
    --urls "http://127.0.0.1:$port" >"$log_file" 2>&1 &
api_pid=$!

for attempt in {1..30}; do
    if curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:$port/health/live" >/dev/null 2>&1; then
        break
    fi
    if ! kill -0 "$api_pid" >/dev/null 2>&1; then
        echo 'API exited before becoming ready.' >&2
        sed -n '1,120p' "$log_file" >&2
        exit 1
    fi
    (( attempt == 30 )) && { echo 'API did not become ready.' >&2; sed -n '1,120p' "$log_file" >&2; exit 1; }
    sleep 1
done

live_headers="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-api-live-headers.XXXXXX")"
live_body="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-api-live-body.XXXXXX")"
ready_body="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-api-ready-body.XXXXXX")"
status_body="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-api-status-body.XXXXXX")"
trap 'rm -f -- "$live_headers" "$live_body" "$ready_body" "$status_body"; cleanup' EXIT

curl --silent --show-error --fail --dump-header "$live_headers" "http://127.0.0.1:$port/health/live" >"$live_body"
curl --silent --show-error --fail "http://127.0.0.1:$port/health/ready" >"$ready_body"
curl --silent --show-error --fail "http://127.0.0.1:$port/operational/status" >"$status_body"

grep -qi '^X-Correlation-Id:' "$live_headers" || { echo 'API did not return X-Correlation-Id.' >&2; exit 1; }
grep -q '"mutatingEndpointsEnabled":false' "$status_body" || { echo 'API status exposed a mutating endpoint.' >&2; exit 1; }
grep -q '"mode":"Normal"' "$status_body" || { echo 'API did not expose its initial operational mode.' >&2; exit 1; }
grep -q '"blocksNewEntries":false' "$status_body" || { echo 'API exposed an unexpected initial block state.' >&2; exit 1; }

echo 'API smoke passed: liveness, readiness, correlation header and read-only operational status verified.'
