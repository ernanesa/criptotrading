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
sensitivity_report_path="$temp_dir/public-oos-sensitivity-report.json"
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
    .schema_version == "public-oos-report/v3" and
    .promotion_authorized == false and
    (.dataset.datasetId == "btcusdt-public-1d") and
    (.dataset.schemaVersion == "public-klines/v1") and
    (.windows | length >= 1) and
    (.strategy_comparison.schema_version == "strategy-comparison/v2") and
    (.strategy_comparison.promotion_authorized == false) and
    (.strategy_comparison.sensitivity_study == null) and
    (.micro_cap_scenario == null) and
    (.strategy_comparison.baseline_no_observed_spread | length == 4) and
    (.strategy_comparison.stress_half_spread_5bps | length == 4) and
    all(.strategy_comparison.baseline_no_observed_spread[];
      (.market_regime_attribution | map(.windowCount) | add) == (.rolling_windows | length))
' "$report_path" >/dev/null

env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj" \
    --no-restore --configuration Release -- \
    --input "$journal_dir" \
    --output "$sensitivity_report_path" \
    --dataset-id btcusdt-public-1d \
    --version "$dataset_version" \
    --source binance-public-data \
    --symbol BTCUSDT \
    --include-sensitivity true

jq -e '
    .strategy_comparison.sensitivity_study.baseline_no_observed_spread.winner_selected == false and
    .strategy_comparison.sensitivity_study.stress_half_spread_5bps.winner_selected == false and
    (.strategy_comparison.sensitivity_study.baseline_no_observed_spread.variants | length == 12) and
    (.strategy_comparison.sensitivity_study.stress_half_spread_5bps.variants | length == 12) and
    all(.strategy_comparison.sensitivity_study.baseline_no_observed_spread.variants[]; has("state_sync_warning"))
' "$sensitivity_report_path" >/dev/null

if invalid_timestamp_output="$(env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj" \
    --no-restore --configuration Release -- \
    --input "$journal_dir" \
    --output "$temp_dir/invalid-snapshot.json" \
    --dataset-id btcusdt-public-1d \
    --version "$dataset_version" \
    --source binance-public-data \
    --symbol BTCUSDT \
    --micro-cap-brl 100 \
    --usdtbrl-price 5.0210 \
    --minimum-order-notional-quote 5 \
    --fx-snapshot-utc 2026-10-11T02:30:48.285 \
    --exchange-rules-snapshot-utc 2026-10-11T02:29:18.468Z 2>&1)"; then
    echo 'Public OOS runner accepted a timezone-less FX snapshot timestamp.' >&2
    exit 1
fi

if [[ "$invalid_timestamp_output" != *"must include an explicit Z or +00:00 suffix"* ]]; then
    echo 'Public OOS runner rejected an FX timestamp for an unexpected reason.' >&2
    exit 1
fi

if stale_snapshot_output="$(env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj" \
    --no-restore --configuration Release -- \
    --input "$journal_dir" \
    --output "$temp_dir/stale-snapshot.json" \
    --dataset-id btcusdt-public-1d \
    --version "$dataset_version" \
    --source binance-public-data \
    --symbol BTCUSDT \
    --micro-cap-brl 100 \
    --usdtbrl-price 5.0210 \
    --minimum-order-notional-quote 5 \
    --fx-snapshot-utc 2020-01-01T00:00:00Z \
    --exchange-rules-snapshot-utc 2020-01-01T00:00:00Z 2>&1)"; then
    echo 'Public OOS runner accepted stale micro-cap snapshots.' >&2
    exit 1
fi

if [[ "$stale_snapshot_output" != *"snapshot is older than the 15-minute research freshness window"* ]]; then
    echo 'Public OOS runner rejected stale snapshots for an unexpected reason.' >&2
    exit 1
fi

if excess_cap_output="$(env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.PublicOosRunner/CryptoTrading.PublicOosRunner.csproj" \
    --no-restore --configuration Release -- \
    --input "$journal_dir" \
    --output "$temp_dir/excess-cap.json" \
    --dataset-id btcusdt-public-1d \
    --version "$dataset_version" \
    --source binance-public-data \
    --symbol BTCUSDT \
    --micro-cap-brl 100.01 \
    --usdtbrl-price 5.0210 \
    --minimum-order-notional-quote 5 \
    --fx-snapshot-utc 2020-01-01T00:00:00Z \
    --exchange-rules-snapshot-utc 2020-01-01T00:00:00Z 2>&1)"; then
    echo 'Public OOS runner accepted a micro-cap above R$100.' >&2
    exit 1
fi

if [[ "$excess_cap_output" != *'must not exceed the R$100 campaign cap'* ]]; then
    echo 'Public OOS runner rejected an over-budget micro-cap for an unexpected reason.' >&2
    exit 1
fi

echo 'Public production-data OOS gate passed: quality-gated walk-forward completed with promotion_authorized=false.'
