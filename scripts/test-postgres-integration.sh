#!/usr/bin/env bash
set -Eeuo pipefail

# Reproducible local persistence evidence. The generated password exists only in
# this process; it is never written to a file, printed, or passed as a CLI arg.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -n "${POSTGRES_INTEGRATION_DOTNET_COMMAND:-}" ]]; then
    if [[ ! -x "$POSTGRES_INTEGRATION_DOTNET_COMMAND" || -d "$POSTGRES_INTEGRATION_DOTNET_COMMAND" ]]; then
        echo "POSTGRES_INTEGRATION_DOTNET_COMMAND must name an executable file." >&2
        exit 1
    fi
    dotnet_command="$POSTGRES_INTEGRATION_DOTNET_COMMAND"
elif [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi
compose_project="${COMPOSE_PROJECT_NAME:-cryptotrading-integration-$$}"
postgres_db="${POSTGRES_DB:-cryptotrading}"
postgres_user="${POSTGRES_USER:-cryptotrading}"
postgres_port="${POSTGRES_TEST_PORT:-55432}"
volume_name="${POSTGRES_VOLUME_NAME:-cryptotrading-postgres-it-$$}"
password="${POSTGRES_TEST_PASSWORD:-}"

# Validate metadata before building a DSN or invoking Compose. Do not allow
# identifier punctuation to become extra connection-string settings.
if [[ ! "$compose_project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] ||
   [[ ! "$volume_name" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] ||
   [[ ! "$postgres_db" =~ ^[a-zA-Z_][a-zA-Z0-9_]*$ ]] ||
   [[ ! "$postgres_user" =~ ^[a-zA-Z_][a-zA-Z0-9_]*$ ]]; then
    echo "Integration resource identifiers have an invalid format." >&2
    exit 1
fi

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

# Serialize cooperating runners for this OS user, including their default
# published port, from preflight through cleanup. No spin/polling on contention.
command -v flock >/dev/null 2>&1 || {
    echo "flock is required to isolate concurrent PostgreSQL integration runs." >&2
    exit 1
}
lock_directory="${POSTGRES_INTEGRATION_LOCK_DIRECTORY:-/tmp/cryptotrading-postgres-integration-$UID}"
if [[ "$lock_directory" != /* || -L "$lock_directory" ]]; then
    echo "Integration lock directory must be an absolute non-symlink path." >&2
    exit 1
fi
if [[ ! -e "$lock_directory" ]]; then
    (umask 077; mkdir -m 700 -- "$lock_directory") || {
        [[ -d "$lock_directory" && ! -L "$lock_directory" ]] || exit 1
    }
fi
if [[ ! -d "$lock_directory" || -L "$lock_directory" ||
      "$(stat -c '%u:%a' -- "$lock_directory")" != "$UID:700" ]]; then
    echo "Integration lock directory must be private and owned by the current user." >&2
    exit 1
fi
lock_file="$lock_directory/harness.lock"
if [[ -L "$lock_file" || ( -e "$lock_file" && ( ! -f "$lock_file" ||
      "$(stat -c '%u:%a' -- "$lock_file")" != "$UID:600" ) ) ]]; then
    echo "Integration lock file must be private, regular and owned by the current user." >&2
    exit 1
fi
umask 077
exec {integration_lock}>"$lock_file"
if ! flock --exclusive --nonblock "$integration_lock"; then
    echo "Another PostgreSQL integration harness is active for this user." >&2
    exit 1
fi

compose=(docker compose --env-file /dev/null --project-name "$compose_project" --file "$repo_root/compose.yaml")
secrets_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-pg-secrets-$$.XXXXXX")"
cleanup() {
    # Deliberately omit --volumes: preserving the named volume is part of this
    # test's contract and allows a later run to prove migration idempotency.
    rm -rf -- "$secrets_dir"
    "${compose[@]}" down --remove-orphans >/dev/null
}

chmod 0700 "$secrets_dir"
printf '%s' "$password" > "$secrets_dir/postgres_password"
chmod 0600 "$secrets_dir/postgres_password"

export POSTGRES_DB="$postgres_db"
export POSTGRES_USER="$postgres_user"
export POSTGRES_PORT="$postgres_port"
export POSTGRES_VOLUME_NAME="$volume_name"
export POSTGRES_SECRETS_DIRECTORY="$secrets_dir"
# This harness starts PostgreSQL only. An explicit empty source keeps the
# required API/Web proxy secret out of this isolated database-only scenario.
export API_INTERNAL_ACCESS_TOKEN_SOURCE="${API_INTERNAL_ACCESS_TOKEN_SOURCE:-/dev/null}"

# The cleanup trap is armed only after read-only collision checks. Otherwise
# a failed preflight could stop a project owned by another process.
if ! existing_containers="$("${compose[@]}" ps --all --quiet)"; then
    echo "Unable to verify isolation of the Compose test project." >&2
    exit 1
fi
if [[ -n "$existing_containers" ]]; then
    echo "Refusing to reuse an existing Compose project for integration tests." >&2
    exit 1
fi
if ! existing_volumes="$(docker volume ls --quiet)"; then
    echo "Unable to enumerate Docker volumes; isolation cannot be verified." >&2
    exit 1
fi
if [[ $'\n'"$existing_volumes"$'\n' == *$'\n'"$volume_name"$'\n'* ]]; then
    echo "Refusing to reuse an existing test volume; choose a new isolated name." >&2
    exit 1
fi

trap cleanup EXIT
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
