#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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
export BINANCE_WITHDRAWALS_ENABLED="${BINANCE_WITHDRAWALS_ENABLED:-false}"

"$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.BinanceReadOnlyProbe/CryptoTrading.BinanceReadOnlyProbe.csproj" \
    --no-restore \
    --configuration Release
