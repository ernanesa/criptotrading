#!/usr/bin/env bash
set -Eeuo pipefail

# Proves the real Worker host can start with durable local persistence and
# apply the current migrations. It intentionally enables PAPER/NO_TRADE only;
# no Binance client or authenticated endpoint is enabled by this harness.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="${COMPOSE_PROJECT_NAME:-cryptotrading-worker-startup}"
postgres_db="${POSTGRES_DB:-cryptotrading}"
postgres_user="${POSTGRES_USER:-cryptotrading}"
postgres_port="${POSTGRES_TEST_PORT:-55433}"
volume_name="${POSTGRES_VOLUME_NAME:-cryptotrading-worker-startup-$$}"
password="${POSTGRES_TEST_PASSWORD:-}"

[[ "$postgres_port" =~ ^[0-9]+$ ]] || { echo 'POSTGRES_TEST_PORT must be numeric.' >&2; exit 2; }
if [[ -z "$password" ]]; then
    command -v openssl >/dev/null 2>&1 || { echo 'openssl is required.' >&2; exit 2; }
    password="$(openssl rand -hex 32)"
fi

dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

compose=(docker compose --project-name "$compose_project" --file "$repo_root/compose.yaml")
worker_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-worker-startup.XXXXXX")"
cleanup() {
    rm -f -- "$worker_log"
    "${compose[@]}" down --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

export POSTGRES_DB="$postgres_db" POSTGRES_USER="$postgres_user" POSTGRES_PASSWORD="$password"
export POSTGRES_PORT="$postgres_port" POSTGRES_VOLUME_NAME="$volume_name"
"${compose[@]}" up -d postgres >/dev/null
for attempt in {1..30}; do
    if "${compose[@]}" exec -T postgres pg_isready -U "$postgres_user" -d "$postgres_db" >/dev/null 2>&1; then
        break
    fi
    (( attempt == 30 )) && { echo 'PostgreSQL did not become ready.' >&2; exit 1; }
    sleep 1
done

export POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=$postgres_port;Database=$postgres_db;Username=$postgres_user;Password=$password"
export WORKER_PERSISTENCE_ENABLED=true WORKER_APPLY_MIGRATIONS=true
export WORKER_EXECUTION_MODE=PAPER WORKER_NO_TRADE=true
export WORKER_RECONCILIATION_ENABLED=false WORKER_ACCOUNT_STREAM_ENABLED=false
export WORKER_RISK_POLICY_REQUIRED=false WORKER_SHADOW_ENABLED=false
export BINANCE_API_KEY= BINANCE_API_SECRET=

set +e
timeout --signal=TERM --kill-after=5s 12s "$dotnet_command" run \
    --project "$repo_root/src/CryptoTrading.Worker/CryptoTrading.Worker.csproj" \
    --no-restore --configuration Release >"$worker_log" 2>&1
worker_status=$?
set -e

# timeout(1) means the host stayed alive until the harness stopped it. A clean
# early exit is also accepted only if the expected migration log was emitted.
if ! rg -q 'Trading persistence migrations applied|Trading PostgreSQL connectivity verified' "$worker_log"; then
    echo 'Worker did not report PostgreSQL connectivity/migration success.' >&2
    sed -n '1,120p' "$worker_log" >&2
    exit 1
fi
if rg -qi 'Unhandled exception|fail(ed)? to start|BINANCE_API_KEY.*required' "$worker_log"; then
    echo 'Worker startup emitted a fatal configuration/runtime error.' >&2
    sed -n '1,120p' "$worker_log" >&2
    exit 1
fi
if [[ "$worker_status" != 0 && "$worker_status" != 124 && "$worker_status" != 143 ]]; then
    echo "Worker exited unexpectedly with status $worker_status." >&2
    exit 1
fi

echo "Worker persistence startup passed: PAPER/NO_TRADE, PostgreSQL migration and audit sink wiring verified (exit=$worker_status)."
