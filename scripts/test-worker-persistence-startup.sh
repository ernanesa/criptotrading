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
worker_assembly="$repo_root/src/CryptoTrading.Worker/bin/Release/net11.0/CryptoTrading.Worker.dll"
[[ -f "$worker_assembly" ]] || { echo 'Worker Release assembly is required; run dotnet build before this startup harness.' >&2; exit 2; }

compose=(docker compose --project-name "$compose_project" --file "$repo_root/compose.yaml")
worker_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-worker-startup.XXXXXX")"
restart_log="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-worker-restart.XXXXXX")"
cleanup() {
    rm -f -- "$worker_log" "$restart_log"
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
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
# No Binance variables are exported: account-stream/reconciliation are disabled
# and the Worker therefore cannot construct an authenticated client.

run_worker() {
    local log_path="$1"
    set +e
    timeout --foreground --signal=TERM --kill-after=15s 12s "$dotnet_command" "$worker_assembly" >"$log_path" 2>&1
    local status=$?
    set -e
    printf '%s' "$status"
}

worker_status="$(run_worker "$worker_log")"

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
    sed -n '1,160p' "$worker_log" >&2
    exit 1
fi

restart_status="$(run_worker "$restart_log")"
if ! rg -q 'Trading persistence migrations applied|Trading PostgreSQL connectivity verified' "$restart_log"; then
    echo 'Restarted Worker did not report PostgreSQL connectivity/migration success.' >&2
    sed -n '1,120p' "$restart_log" >&2
    exit 1
fi
if rg -qi 'Unhandled exception|fail(ed)? to start|BINANCE_API_KEY.*required' "$restart_log"; then
    echo 'Restarted Worker emitted a fatal configuration/runtime error.' >&2
    sed -n '1,120p' "$restart_log" >&2
    exit 1
fi
if [[ "$restart_status" != 0 && "$restart_status" != 124 && "$restart_status" != 143 ]]; then
    echo "Restarted Worker exited unexpectedly with status $restart_status." >&2
    sed -n '1,160p' "$restart_log" >&2
    exit 1
fi

runtime_sequence="$("${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" -Atqc \
    "SELECT string_agg(revision::text || ':' || from_state || '>' || to_state, ',' ORDER BY revision) FROM trading.runtime_state_transitions;")"
runtime_checkpoint="$("${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" -Atqc \
    "SELECT state || ':' || revision::text FROM trading.runtime_state_checkpoints WHERE scope = 'worker';")"
expected_sequence='1:Stopped>Bootstrapping,2:Bootstrapping>Stopping,3:Stopping>Stopped,4:Stopped>Bootstrapping,5:Bootstrapping>SafeMode,6:SafeMode>Stopping,7:Stopping>Stopped'
if [[ "$runtime_sequence" != "$expected_sequence" || "$runtime_checkpoint" != 'Stopped:7' ]]; then
    echo 'Worker runtime checkpoint did not restore fail-closed after migration-backed restart.' >&2
    echo "observed transitions: $runtime_sequence" >&2
    echo "observed checkpoint: $runtime_checkpoint" >&2
    exit 1
fi

echo "Worker persistence startup passed: PAPER/NO_TRADE, migrations, checkpoint-backed fail-closed restart and audit sink wiring verified (exits=$worker_status/$restart_status)."
