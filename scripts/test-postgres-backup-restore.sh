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
source_manifest="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-backup.XXXXXX.source")"
restored_manifest="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-backup.XXXXXX.restored")"
report_dir="$repo_root/artifacts/recovery"
run_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
report_file="$report_dir/postgres-rpo-rto-$run_stamp-$$.md"
backup_cli_output_directory="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-backup-cli.XXXXXX")"
password="$(openssl rand -hex 32)"
compose=(docker compose --env-file /dev/null --project-name "$compose_project" --file "$repo_root/compose.yaml")

cleanup() {
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -f -- "$backup_file"
    rm -f -- "$checksum_file" "$corrupt_backup_file" "$source_manifest" "$restored_manifest"
    rm -rf -- "$backup_cli_output_directory"
}
trap cleanup EXIT

mkdir -p -- "$report_dir"
umask 077
chmod 0700 "$backup_cli_output_directory"

now_ns() { date +%s%N; }
elapsed_ms() { printf '%s' "$(( ($2 - $1) / 1000000 ))"; }

export POSTGRES_DB="$postgres_db"
export POSTGRES_USER="$postgres_user"
export POSTGRES_PASSWORD="$password"
export POSTGRES_PORT="$postgres_port"
export POSTGRES_VOLUME_NAME="$volume_name"
# This harness starts PostgreSQL only; it never starts API/Web.
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

# Apply the repository's complete migration chain and seed deterministic,
# non-sensitive financial state. The fixture models a committed journal entry,
# balanced ledger postings, its projection, and an outbox event; no exchange or
# worker is started. The generated password never appears in command arguments.
for migration in "$repo_root"/src/CryptoTrading.Persistence/Migrations/*.up.sql; do
    "${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" < "$migration" >/dev/null
done
drill_started_ns="$(now_ns)"
"${compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_db" >/dev/null <<'SQL'
BEGIN;
INSERT INTO trading.journal_entries (entry_id, correlation_id, occurred_at, reason, idempotency_key)
VALUES ('74190000-0000-4000-8000-000000000001', '74190000-0000-4000-8000-000000000002', transaction_timestamp(), 'backup restore deterministic fixture', 'backup-drill-7419');
INSERT INTO trading.ledger_postings (entry_id, posting_index, account_id, asset, amount)
VALUES ('74190000-0000-4000-8000-000000000001', 0, 'backup-drill:cash', 'USDT', 1000.000000000000000000),
       ('74190000-0000-4000-8000-000000000001', 1, 'backup-drill:counterparty', 'USDT', -1000.000000000000000000);
INSERT INTO trading.ledger_balances (account_id, asset, balance, projection_version)
VALUES ('backup-drill:cash', 'USDT', 1000.000000000000000000, 1),
       ('backup-drill:counterparty', 'USDT', -1000.000000000000000000, 1);
INSERT INTO trading.outbox_messages (message_id, aggregate_type, aggregate_id, event_type, payload, correlation_id, idempotency_key, occurred_at)
VALUES ('74190000-0000-4000-8000-000000000003', 'LedgerEntry', '74190000-0000-4000-8000-000000000001', 'BackupDrillFixtureRecorded', '{"fixture":true,"environment":"local"}'::jsonb, '74190000-0000-4000-8000-000000000002', 'backup-drill-event-7419', transaction_timestamp());
COMMIT;
SQL

# Hash every table in the financial schema independently so failures identify
# the affected relation without copying financial row contents into evidence.
table_manifest() {
    local database="$1" output_file="$2" table
    : > "$output_file"
    while IFS= read -r table; do
        [[ -n "$table" ]] || continue
        local result
        result="$("${compose[@]}" exec -T postgres psql -At -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$database" \
            -c "SELECT count(*) || ' ' || md5(coalesce(string_agg(to_jsonb(t)::text, E'\\n' ORDER BY to_jsonb(t)::text), '')) FROM trading.$table AS t;" </dev/null)"
        printf '%s %s\n' "$table" "${result//$'\r'/}" >> "$output_file"
    done < <("${compose[@]}" exec -T postgres psql -At -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$database" \
        -c "SELECT tablename FROM pg_tables WHERE schemaname = 'trading' ORDER BY tablename;")
}
table_manifest "$postgres_db" "$source_manifest"
if (( $(wc -l < "$source_manifest") < 20 )) || ! awk '$1 == "journal_entries" && $2 > 0 { journal=1 } $1 == "ledger_postings" && $2 >= 2 { postings=1 } $1 == "outbox_messages" && $2 > 0 { outbox=1 } END { exit !(journal && postings && outbox) }' "$source_manifest"; then
    echo "Backup/restore manifest is incomplete or financial fixture is absent." >&2
    exit 1
fi

# Exercise the operational CLI against only this ephemeral PostgreSQL fixture.
# The CLI itself runs pg_restore --list before atomically publishing its dump;
# this drill keeps the existing full restore and corruption probes below.
postgres_container_id="$("${compose[@]}" ps -q postgres)"
[[ -n "$postgres_container_id" ]] || { echo 'Ephemeral PostgreSQL container ID is unavailable.' >&2; exit 1; }
"$repo_root/scripts/backup-postgres.sh" \
    --container "$postgres_container_id" \
    --database "$postgres_db" \
    --user "$postgres_user" \
    --output-directory "$backup_cli_output_directory" \
    --min-free-bytes 1048576 >/dev/null
backup_cli_artifact="$(find "$backup_cli_output_directory" -mindepth 1 -maxdepth 1 -type d -name 'postgres-*' -print -quit)"
[[ -n "$backup_cli_artifact" && -s "$backup_cli_artifact/backup.dump" && -s "$backup_cli_artifact/backup.dump.sha256" && -s "$backup_cli_artifact/manifest.json" ]] || {
    echo 'Operational backup CLI did not publish a complete verified artifact.' >&2
    exit 1
}
[[ "$(stat -c '%a' "$backup_cli_artifact")" == 700 && "$(stat -c '%a' "$backup_cli_artifact/backup.dump")" == 600 ]]
(cd "$backup_cli_artifact" && sha256sum --check backup.dump.sha256 >/dev/null)
grep -Fq '"verification":"pg_restore_list"' "$backup_cli_artifact/manifest.json"

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
table_manifest cryptotrading_restore "$restored_manifest"
verify_finished_ns="$(now_ns)"
if ! cmp -s "$source_manifest" "$restored_manifest"; then
    diff -u "$source_manifest" "$restored_manifest" >&2 || true
    echo "Backup/restore verification failed: trading table row counts or hashes differ." >&2
    exit 1
fi

drill_finished_ns="$(now_ns)"
backup_ms="$(elapsed_ms "$backup_started_ns" "$backup_finished_ns")"
restore_ms="$(elapsed_ms "$restore_started_ns" "$restore_finished_ns")"
verify_ms="$(elapsed_ms "$verify_started_ns" "$verify_finished_ns")"
total_ms="$(elapsed_ms "$drill_started_ns" "$drill_finished_ns")"
backup_bytes="$(wc -c < "$backup_file" | tr -d '[:space:]')"
table_count="$(wc -l < "$source_manifest" | tr -d '[:space:]')"
financial_rows="$(awk '{ total += $2 } END { print total + 0 }' "$source_manifest")"

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
- Financial state verification: ${financial_rows} rows across ${table_count} trading tables; per-table row counts and canonical row hashes matched before and after restore.
- RPO probe: 0 seeded financial rows lost in the restored snapshot; this is a deterministic local result, not a production RPO guarantee.
- Verification: complete trading schema data manifest matched.
- Cleanup: ephemeral containers, volume, restore database, and dump file were removed.
EOF
chmod 600 "$report_file"
echo "PostgreSQL backup/restore drill passed; RPO/RTO report: $report_file"
