#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet_command="$HOME/.dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    echo 'Refusing public OOS run while Binance credentials are present in the environment.' >&2
    exit 2
fi

temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-public-oos.XXXXXX")"
cleanup() {
    rm -rf -- "$temp_dir"
}
trap cleanup EXIT

journal_dir="$temp_dir/journal"
report_path="$temp_dir/public-oos-report.json"
dataset_version="$(date -u +%Y-%m-%d)"

env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicKlinesRecorder/CryptoTrading.PublicKlinesRecorder.csproj" \
    --no-restore --configuration Release -- \
    --source production-data \
    --symbol BTCUSDT \
    --interval 1d \
    --output "$journal_dir" \
    --max-bars 180

env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj" \
    --no-restore --configuration Release -- \
    --input "$journal_dir" \
    --output "$report_path" \
    --dataset-id btcusdt-public-1d \
    --version "$dataset_version" \
    --source binance-public-data \
    --symbol BTCUSDT

if ! command -v jq >/dev/null 2>&1; then
    echo 'Public OOS run completed, but jq is required for the promotion guard.' >&2
    exit 2
fi

jq -e '
    .promotion_authorized == false and
    (.dataset.datasetId == "btcusdt-public-1d") and
    (.dataset.schemaVersion == "public-klines/v1") and
    (.windows | length >= 1)
' "$report_path" >/dev/null

echo 'Public production-data OOS gate passed: quality-gated walk-forward completed with promotion_authorized=false.'
