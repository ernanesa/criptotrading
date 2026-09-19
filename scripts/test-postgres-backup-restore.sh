#!/usr/bin/env bash
set -Eeuo pipefail

# Destructive operations are limited to a uniquely named, ephemeral integration
# project and volume created by this script. No application volume is targeted.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="cryptotrading-backup-$$"
postgres_db="cryptotrading"
postgres_user="cryptotrading"
postgres_port=$((55000 + ($$ % 1000)))
volume_name="cryptotrading-backup-$$"
backup_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-backup.XXXXXX.dump")"
password="$(openssl rand -hex 32)"
compose=(docker compose --project-name "$compose_project" --file "$repo_root/compose.yaml")

cleanup() {
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -f -- "$backup_file"
}
trap cleanup EXIT

export POSTGRES_DB="$postgres_db"
export POSTGRES_USER="$postgres_user"
export POSTGRES_PASSWORD="$password"
export POSTGRES_PORT="$postgres_port"
export POSTGRES_VOLUME_NAME="$volume_name"

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

# Create a non-sensitive marker, dump it, restore into a second database and
# verify the row. The generated password never appears in command arguments.
"${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" \
    -c 'CREATE TABLE backup_drill_marker (id integer PRIMARY KEY, status text NOT NULL);' \
    -c "INSERT INTO backup_drill_marker VALUES (1, 'ok');" >/dev/null
"${compose[@]}" exec -T postgres pg_dump -U "$postgres_user" -d "$postgres_db" --format=custom > "$backup_file"
"${compose[@]}" exec -T postgres createdb -U "$postgres_user" cryptotrading_restore
"${compose[@]}" exec -T postgres pg_restore -U "$postgres_user" --dbname=cryptotrading_restore < "$backup_file"
restored_status="$("${compose[@]}" exec -T postgres psql -At -U "$postgres_user" -d cryptotrading_restore -c 'SELECT status FROM backup_drill_marker WHERE id = 1;')"
if [[ "${restored_status//$'\r'/}" != "ok" ]]; then
    echo "Backup/restore verification failed." >&2
    exit 1
fi

echo "PostgreSQL backup/restore drill passed; only ephemeral resources were removed."
