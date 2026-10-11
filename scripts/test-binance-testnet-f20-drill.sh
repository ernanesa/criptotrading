#!/usr/bin/env bash
set -Eeuo pipefail

# Supervised F20 Binance Spot Testnet lifecycle drill:
# submit/cancel, submit/fill/flatten, reject, ambiguous/LockedUnknown, restart, reconcile, stop.
# Requires explicit operator authorization (TESTNET_F20_DRILL_AUTHORIZED=true).
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ "${TESTNET_F20_DRILL_AUTHORIZED:-false}" != "true" ]]; then
    echo "Refusing to run: TESTNET_F20_DRILL_AUTHORIZED=true is required immediately before mutable Testnet execution." >&2
    exit 2
fi

if [[ -f "$repo_root/.env.testnet" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$repo_root/.env.testnet"
    set +a
fi

"$repo_root/scripts/check-testnet-secret-storage.sh" >/dev/null

dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

compose_project="${COMPOSE_PROJECT_NAME:-cryptotrading-f20-drill-$$}"
postgres_db="${POSTGRES_DB:-cryptotrading}"
postgres_user="${POSTGRES_USER:-cryptotrading}"
postgres_port="${POSTGRES_TEST_PORT:-55439}"
volume_name="${POSTGRES_VOLUME_NAME:-cryptotrading-f20-pg-$$}"
password="$(openssl rand -hex 32)"

compose=(docker compose --env-file /dev/null --project-name "$compose_project" --file "$repo_root/compose.yaml")
secrets_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-f20-pg-secrets-$$.XXXXXX")"
drill_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-f20-drill-log-$$.XXXXXX")"

cleanup() {
    rm -rf -- "$secrets_dir"
    rm -f -- "$drill_log"
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

chmod 0700 "$secrets_dir"
printf '%s' "$password" > "$secrets_dir/postgres_password"
chmod 0600 "$secrets_dir/postgres_password"

export POSTGRES_DB="$postgres_db"
export POSTGRES_USER="$postgres_user"
export POSTGRES_PORT="$postgres_port"
export POSTGRES_VOLUME_NAME="$volume_name"
export POSTGRES_SECRETS_DIRECTORY="$secrets_dir"
export API_INTERNAL_ACCESS_TOKEN_SOURCE="/dev/null"

"${compose[@]}" up -d postgres >/dev/null
for attempt in {1..30}; do
    if "${compose[@]}" exec -T postgres pg_isready -h 127.0.0.1 -U "$postgres_user" -d "$postgres_db" >/dev/null 2>&1; then
        break
    fi
    (( attempt == 30 )) && { echo "PostgreSQL did not become ready." >&2; exit 1; }
    sleep 1
done

export POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=$postgres_port;Database=$postgres_db;Username=$postgres_user;Password=$password"
export TESTNET_F20_DRILL_AUTHORIZED="true"

set +e
"$dotnet_command" run \
    --project "$repo_root/tools/CryptoTrading.TestnetStateInitializer/CryptoTrading.TestnetStateInitializer.csproj" \
    --no-build \
    --no-restore \
    --configuration Release \
    -- --run-f20-lifecycle-drill >"$drill_log" 2>&1
drill_status=$?
set -e

report_dir="$repo_root/artifacts/binance"
mkdir -p -- "$report_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/testnet-f20-lifecycle-drill-$timestamp-$$.md"

result="FAIL"
if (( drill_status == 0 )); then
    result="PASS"
fi

summary_json="$(tail -n 1 "$drill_log" | tr -d '\r')"

umask 077
{
    echo "# Binance Spot Testnet F20 Lifecycle Drill Report"
    echo
    echo "- Result: **$result**"
    echo "- UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`"
    echo "- Environment: \`TESTNET\` (\`https://testnet.binance.vision/api\`)"
    echo "- Symbol: \`BTCUSDT\`"
    echo "- Operator Authorization: \`explicit immediate approval (Ernane)\`"
    echo "- Credentials: \`protected file-scoped (0600), never printed or logged\`"
    echo "- Exit Code: \`$drill_status\`"
    echo "- Redacted Drill Summary JSON:"
    echo
    echo "\`\`\`json"
    echo "$summary_json"
    echo "\`\`\`"
} > "$report_file"
chmod 600 "$report_file"

echo "F20 Testnet lifecycle drill report: $report_file"
if (( drill_status != 0 )); then
    echo "F20 Testnet lifecycle drill failed." >&2
    exit "$drill_status"
fi
echo "Binance Spot Testnet F20 lifecycle drill completed successfully."
