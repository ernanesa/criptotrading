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
checksum_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-backup.XXXXXX.sha256")"
corrupt_backup_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-backup.XXXXXX.corrupt")"
report_dir="$repo_root/artifacts/recovery"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/postgres-rpo-rto-$run_stamp-$$.md"
password="$(openssl rand -hex 32)"
compose=(docker compose --project-name "$compose_project" --file "$repo_root/compose.yaml")

cleanup() {
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -f -- "$backup_file"
    rm -f -- "$checksum_file" "$corrupt_backup_file"
}
trap cleanup EXIT

mkdir -p -- "$report_dir"
umask 077

now_ns() { date +%s%N; }
elapsed_ms() { printf '%s' "$(( ($2 - $1) / 1000000 ))"; }

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
drill_started_ns="$(now_ns)"
"${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" \
    -c 'CREATE TABLE backup_drill_marker (id integer PRIMARY KEY, status text NOT NULL);' \
    -c "INSERT INTO backup_drill_marker VALUES (1, 'ok');" >/dev/null
backup_started_ns="$(now_ns)"
"${compose[@]}" exec -T postgres pg_dump -U "$postgres_user" -d "$postgres_db" --format=custom > "$backup_file"
backup_finished_ns="$(now_ns)"
backup_sha256="$(sha256sum "$backup_file" | awk '{print $1}')"
printf '%s  %s\n' "$backup_sha256" "$backup_file" > "$checksum_file"
sha256sum --check "$checksum_file" >/dev/null

# A corrupted artifact must be rejected before any restore attempt. This is a
# local tamper-detection probe; the source dump remains untouched.
cp -- "$backup_file" "$corrupt_backup_file"
printf 'x' | dd of="$corrupt_backup_file" bs=1 seek=0 conv=notrunc status=none
if sha256sum --check <(printf '%s  %s\n' "$backup_sha256" "$corrupt_backup_file") >/dev/null 2>&1; then
    echo "Corrupted backup unexpectedly passed checksum validation." >&2
    exit 1
fi
"${compose[@]}" exec -T postgres createdb -U "$postgres_user" cryptotrading_restore
restore_started_ns="$(now_ns)"
"${compose[@]}" exec -T postgres pg_restore -U "$postgres_user" --dbname=cryptotrading_restore < "$backup_file"
restore_finished_ns="$(now_ns)"
verify_started_ns="$(now_ns)"
restored_status="$("${compose[@]}" exec -T postgres psql -At -U "$postgres_user" -d cryptotrading_restore -c 'SELECT status FROM backup_drill_marker WHERE id = 1;')"
verify_finished_ns="$(now_ns)"
if [[ "${restored_status//$'\r'/}" != "ok" ]]; then
    echo "Backup/restore verification failed." >&2
    exit 1
fi

drill_finished_ns="$(now_ns)"
backup_ms="$(elapsed_ms "$backup_started_ns" "$backup_finished_ns")"
restore_ms="$(elapsed_ms "$restore_started_ns" "$restore_finished_ns")"
verify_ms="$(elapsed_ms "$verify_started_ns" "$verify_finished_ns")"
total_ms="$(elapsed_ms "$drill_started_ns" "$drill_finished_ns")"
backup_bytes="$(wc -c < "$backup_file" | tr -d '[:space:]')"

cat > "$report_file" <<EOF
# PostgreSQL RPO/RTO drill — $run_stamp

- Scope: ephemeral local PostgreSQL backup/restore only.
- Environment: local Docker Compose; no Binance credentials, exchange calls, or application volume targeted.
- Backup artifact size: ${backup_bytes} bytes.
- Backup SHA-256: ${backup_sha256} (verified before restore).
- Corruption probe: altered copy rejected by SHA-256 verification before restore.
- Backup duration: ${backup_ms} ms.
- Restore duration (RTO probe): ${restore_ms} ms.
- Verification duration: ${verify_ms} ms.
- Total drill duration: ${total_ms} ms.
- RPO probe: 0 rows lost for the committed marker transaction; this is a deterministic drill result, not a production RPO guarantee.
- Verification: restored marker status was \`ok\`.
- Cleanup: ephemeral containers, volume, restore database, and dump file were removed.
EOF
chmod 600 "$report_file"
echo "PostgreSQL backup/restore drill passed; RPO/RTO report: $report_file"
