#!/usr/bin/env bash
set -Eeuo pipefail

# Reproducible local persistence evidence. The generated password exists only in
# this process; it is never written to a file, printed, or passed as a CLI arg.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi
compose_project="${COMPOSE_PROJECT_NAME:-cryptotrading-integration}"
postgres_db="${POSTGRES_DB:-cryptotrading}"
postgres_user="${POSTGRES_USER:-cryptotrading}"
postgres_port="${POSTGRES_TEST_PORT:-55432}"
volume_name="${POSTGRES_VOLUME_NAME:-cryptotrading-postgres-it-$$}"
password="${POSTGRES_TEST_PASSWORD:-}"

if [[ -z "$password" ]]; then
    command -v openssl >/dev/null 2>&1 || {
        echo "openssl is required to generate the ephemeral PostgreSQL password." >&2
        exit 1
    }
    password="$(openssl rand -hex 32)"
fi

if [[ ! "$postgres_port" =~ ^[0-9]+$ ]] || (( postgres_port < 1024 || postgres_port > 65535 )); then
    echo "POSTGRES_TEST_PORT must be a TCP port between 1024 and 65535." >&2
    exit 1
fi

compose=(docker compose --env-file /dev/null --project-name "$compose_project" --file "$repo_root/compose.yaml")
cleanup() {
    # Deliberately omit --volumes: preserving the named volume is part of this
    # test's contract and allows a later run to prove migration idempotency.
    "${compose[@]}" down --remove-orphans >/dev/null
}
trap cleanup EXIT

export POSTGRES_DB="$postgres_db"
export POSTGRES_USER="$postgres_user"
export POSTGRES_PASSWORD="$password"
export POSTGRES_PORT="$postgres_port"
export POSTGRES_VOLUME_NAME="$volume_name"
# This harness starts PostgreSQL only. An explicit empty source keeps the
# required API/Web proxy secret out of this isolated database-only scenario.
export API_INTERNAL_ACCESS_TOKEN_SOURCE="${API_INTERNAL_ACCESS_TOKEN_SOURCE:-/dev/null}"

"${compose[@]}" up -d postgres >/dev/null
for attempt in {1..30}; do
    if "${compose[@]}" exec -T postgres pg_isready -U "$postgres_user" -d "$postgres_db" >/dev/null 2>&1; then
        break
    fi
    if (( attempt == 30 )); then
        echo "PostgreSQL did not become ready within 30 attempts." >&2
        exit 1
    fi
    sleep 1
done

connection_string="Host=127.0.0.1;Port=$postgres_port;Database=$postgres_db;Username=$postgres_user;Password=$password"
export POSTGRES_CONNECTION_STRING="$connection_string"

"$dotnet_command" test "$repo_root/tests/CryptoTrading.Persistence.IntegrationTests/CryptoTrading.Persistence.IntegrationTests.csproj" \
    --minimum-expected-tests 1

"${compose[@]}" down --remove-orphans >/dev/null
trap - EXIT
if ! docker volume inspect "$volume_name" >/dev/null 2>&1; then
    echo "Expected named volume $volume_name to remain after compose down." >&2
    exit 1
fi

echo "PostgreSQL integration evidence passed; compose stopped and named volume preserved."
