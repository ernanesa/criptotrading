#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="${DOTNET_COMMAND:-dotnet}"
compose_project="${COMPOSE_PROJECT_NAME:-cryptotrading-governed-evidence}"
postgres_port="${POSTGRES_TEST_PORT:-55433}"
postgres_db="cryptotrading"
postgres_user="cryptotrading"
volume_name="cryptotrading-governed-evidence-$$"
password="${POSTGRES_TEST_PASSWORD:-}"

if [[ -z "$password" ]]; then
    password="$(openssl rand -hex 32)"
fi

compose=(docker compose --project-name "$compose_project" --file "$repo_root/compose.yaml")
cleanup() { "${compose[@]}" down --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

export POSTGRES_DB="$postgres_db" POSTGRES_USER="$postgres_user" POSTGRES_PASSWORD="$password"
export POSTGRES_PORT="$postgres_port" POSTGRES_VOLUME_NAME="$volume_name"
"${compose[@]}" up -d postgres >/dev/null
for attempt in {1..30}; do
    if "${compose[@]}" exec -T postgres pg_isready -U "$postgres_user" -d "$postgres_db" >/dev/null 2>&1; then break; fi
    if (( attempt == 30 )); then echo "PostgreSQL did not become ready." >&2; exit 1; fi
    sleep 1
done

connection_string="Host=127.0.0.1;Port=$postgres_port;Database=$postgres_db;Username=$postgres_user;Password=$password"
export POSTGRES_CONNECTION_STRING="$connection_string"
dataset_dir="$(mktemp -d /tmp/cryptotrading-governed-dataset.XXXXXX)"
evidence_dir="$(mktemp -d /tmp/cryptotrading-governed-evidence.XXXXXX)"
unset BINANCE_API_KEY BINANCE_API_SECRET

"$dotnet_command" run --project "$repo_root/tools/CryptoTrading.PublicKlinesRecorder" -c Release -- \
    --source production-data --symbol BTCUSDT --interval 1d --output "$dataset_dir" --max-bars 180 >/dev/null
experiment_id="$("$dotnet_command" run --project "$repo_root/tools/CryptoTrading.GovernedEvidenceFixture" -c Release -- \
    --input "$dataset_dir" --dataset-id btcusdt-public-1d --dataset-version 2026-09-19 \
    --dataset-source binance-public-data --symbol BTCUSDT)"
"$dotnet_command" run --project "$repo_root/tools/CryptoTrading.GovernedEvidenceRunner" -c Release -- \
    --experiment-id "$experiment_id" --input "$dataset_dir" --output "$evidence_dir" \
    --dataset-id btcusdt-public-1d --dataset-version 2026-09-19 --dataset-source binance-public-data \
    --symbol BTCUSDT --decision Rejected --trace docs/governed-evidence-runner.md --trace AC-GOVERNED-E2E-01

test -s "$evidence_dir/$experiment_id.experiment.json"
test -s "$evidence_dir/$experiment_id.experiment.manifest.json"
jq -e '.promotion_authorized == false' "$evidence_dir/$experiment_id.experiment.manifest.json" >/dev/null
echo "Governed evidence runner E2E passed: PostgreSQL snapshot -> immutable bundle (promotion_authorized=false)."
