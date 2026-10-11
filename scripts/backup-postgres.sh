#!/usr/bin/env bash
set -Eeuo pipefail

# Creates one verified PostgreSQL logical-backup artifact. This tool never
# restores data, reads .env files, or selects a container/database implicitly.

usage() {
    echo 'Usage: backup-postgres.sh --container NAME --database NAME --user NAME --output-directory ABSOLUTE_PATH --min-free-bytes BYTES [--encryption-policy local-private-unencrypted-approved|aes256-keyfile] [--encryption-key-file ABSOLUTE_PATH] [--retention-policy no-automatic-deletion-approved|prune-after-verified-7d] [--retention-days DAYS]' >&2
}

container=''
database=''
database_user=''
output_directory=''
min_free_bytes=''
encryption_policy='local-private-unencrypted-approved'
encryption_key_file="${POSTGRES_BACKUP_KEY_FILE:-}"
retention_policy='no-automatic-deletion-approved'
retention_days="${BACKUP_RETENTION_DAYS:-7}"
repo_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

while (($#)); do
    case "$1" in
        --container|--database|--user|--output-directory|--min-free-bytes|--encryption-policy|--encryption-key-file|--retention-policy|--retention-days)
            (($# >= 2)) || { usage; exit 2; }
            case "$1" in
                --container) container="$2" ;;
                --database) database="$2" ;;
                --user) database_user="$2" ;;
                --output-directory) output_directory="$2" ;;
                --min-free-bytes) min_free_bytes="$2" ;;
                --encryption-policy) encryption_policy="$2" ;;
                --encryption-key-file) encryption_key_file="$2" ;;
                --retention-policy) retention_policy="$2" ;;
                --retention-days) retention_days="$2" ;;
            esac
            shift 2
            ;;
        --help) usage; exit 0 ;;
        *) usage; exit 2 ;;
    esac
done

[[ -n "$container" && -n "$database" && -n "$database_user" && -n "$output_directory" && -n "$min_free_bytes" ]] || { usage; exit 2; }
[[ "$output_directory" == /* && "$min_free_bytes" =~ ^[0-9]+$ && "$retention_days" =~ ^[1-9][0-9]*$ ]] || { usage; exit 2; }
if [[ "$encryption_policy" != 'local-private-unencrypted-approved' && "$encryption_policy" != 'aes256-keyfile' ]]; then
    echo 'Unsupported --encryption-policy; use local-private-unencrypted-approved or aes256-keyfile.' >&2
    exit 2
fi
if [[ "$retention_policy" != 'no-automatic-deletion-approved' && "$retention_policy" != 'prune-after-verified-7d' ]]; then
    echo 'Unsupported --retention-policy; use no-automatic-deletion-approved or prune-after-verified-7d.' >&2
    exit 2
fi

check_no_symlink_path() {
    local path="$1" component current='/'
    IFS='/' read -r -a components <<< "${path#/}"
    for component in "${components[@]}"; do
        [[ -n "$component" ]] || continue
        current+="$component"
        [[ ! -L "$current" && -d "$current" ]] || return 1
        current+='/'
    done
}

if ! check_no_symlink_path "$output_directory"; then
    echo 'Backup output directory must exist and contain no symbolic-link path component.' >&2
    exit 2
fi

output_directory="$(cd -P -- "$output_directory" && pwd)"
output_mode="$(stat -c '%a' -- "$output_directory" 2>/dev/null)"
output_owner="$(stat -c '%u' -- "$output_directory" 2>/dev/null)"
if [[ "$output_directory" == / || "$output_directory" == "$repo_root" || "$output_directory" == "$repo_root"/* ||
      "$output_owner" != "$(id -u)" || ! "$output_mode" =~ ^[0-7]{3,4}$ ]]; then
    echo 'Backup output directory must be user-owned, private, outside the repository, and not the filesystem root.' >&2
    exit 2
fi
if (( 8#$output_mode & 077 )); then
    echo 'Backup output directory must be user-owned, private, outside the repository, and not the filesystem root.' >&2
    exit 2
fi
if ! command -v git >/dev/null 2>&1 || git -C "$output_directory" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    echo 'Backup output directory must be outside every Git worktree.' >&2
    exit 2
fi

if [[ "$encryption_policy" == 'aes256-keyfile' ]]; then
    [[ -n "$encryption_key_file" && "$encryption_key_file" == /* ]] || {
        echo 'An absolute --encryption-key-file path is required for aes256-keyfile encryption.' >&2
        exit 2
    }
    key_parent="$(dirname -- "$encryption_key_file")"
    if ! check_no_symlink_path "$key_parent" || [[ -L "$encryption_key_file" || ! -f "$encryption_key_file" || ! -s "$encryption_key_file" ]]; then
        echo 'Encryption key file must be a non-empty regular file in a non-symlink directory.' >&2
        exit 2
    fi
    key_stat="$(stat -c '%u:%a' -- "$encryption_key_file" 2>/dev/null || true)"
    key_parent_mode="$(stat -c '%a' -- "$key_parent" 2>/dev/null || true)"
    key_parent_owner="$(stat -c '%u' -- "$key_parent" 2>/dev/null || true)"
    if [[ "$key_stat" != "$(id -u):600" || "$key_parent_owner" != "$(id -u)" || ! "$key_parent_mode" =~ ^[0-7]{3,4}$ ]] || (( 8#$key_parent_mode & 077 )); then
        echo 'Encryption key file must be mode 0600 inside a mode 0700 user-owned directory.' >&2
        exit 2
    fi
fi

umask 077
backup_id="postgres-$(date -u +%Y%m%dT%H%M%SZ)-$$"
started_epoch="$(date +%s)"
staging_directory=''
published_directory=''
failed=0

log_event() {
    local status="$1" bytes="$2" verification="$3" reason="$4"
    local elapsed_seconds=$(( $(date +%s) - started_epoch ))
    printf '{"timestamp":"%s","status":"%s","backupId":"%s","bytes":%s,"durationSeconds":%s,"verification":"%s","encryptionPolicy":"%s","retentionPolicy":"%s","reason":"%s"}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$status" "$backup_id" "$bytes" "$elapsed_seconds" "$verification" "$encryption_policy" "$retention_policy" "$reason" >&2
}

cleanup() {
    if [[ -n "$staging_directory" && -d "$staging_directory" ]]; then
        rm -rf -- "$staging_directory" >/dev/null 2>&1 || true
    fi
    if (( failed )); then
        log_event 'failed' 0 'not_run' "${failure_reason:-unexpected_failure}"
    fi
}
trap cleanup EXIT
trap 'failed=1; failure_reason="${failure_reason:-unexpected_failure}"' ERR

fail() {
    failed=1
    failure_reason="$1"
    exit 1
}

available_bytes="$(df -PB1 -- "$output_directory" 2>/dev/null | awk 'NR == 2 { print $4 }')"
[[ "$available_bytes" =~ ^[0-9]+$ && "$available_bytes" -ge "$min_free_bytes" ]] || fail 'insufficient_free_space'

if ! staging_directory="$(mktemp -d "$output_directory/.postgres-backup-staging.XXXXXX" 2>/dev/null)"; then
    fail 'staging_create_failed'
fi
chmod 0700 "$staging_directory"
archive="$staging_directory/backup.dump"
checksum="$staging_directory/backup.dump.sha256"
manifest="$staging_directory/manifest.json"
published_directory="$output_directory/$backup_id"
[[ ! -e "$published_directory" && ! -L "$published_directory" ]] || fail 'backup_id_collision'

if ! docker exec -i --user postgres "$container" pg_dump --username "$database_user" --dbname "$database" --format=custom > "$archive" 2>/dev/null; then
    fail 'pg_dump_failed'
fi
chmod 0600 "$archive"
[[ -s "$archive" ]] || fail 'empty_archive'

if ! docker exec -i --user postgres "$container" pg_restore --list < "$archive" >/dev/null 2>/dev/null; then
    fail 'pg_restore_verification_failed'
fi

raw_bytes="$(wc -c < "$archive" | tr -d '[:space:]')"
raw_sha256="$(sha256sum "$archive" 2>/dev/null | awk '{ print $1 }')"
[[ "$raw_bytes" =~ ^[1-9][0-9]*$ && "$raw_sha256" =~ ^[a-f0-9]{64}$ ]] || fail 'archive_integrity_failed'

if [[ "$encryption_policy" == 'aes256-keyfile' ]]; then
    encrypted_archive="$staging_directory/backup.dump.enc"
    encrypted_checksum="$staging_directory/backup.dump.enc.sha256"
    if ! openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$encryption_key_file" -in "$archive" -out "$encrypted_archive" 2>/dev/null; then
        fail 'encryption_failed'
    fi
    chmod 0600 "$encrypted_archive"
    if ! openssl enc -d -aes-256-cbc -pbkdf2 -salt -pass "file:$encryption_key_file" -in "$encrypted_archive" 2>/dev/null | \
        docker exec -i --user postgres "$container" pg_restore --list >/dev/null 2>/dev/null; then
        fail 'encrypted_restore_verification_failed'
    fi
    rm -f -- "$archive"
    archive_bytes="$(wc -c < "$encrypted_archive" | tr -d '[:space:]')"
    archive_sha256="$(sha256sum "$encrypted_archive" 2>/dev/null | awk '{ print $1 }')"
    [[ "$archive_bytes" =~ ^[1-9][0-9]*$ && "$archive_sha256" =~ ^[a-f0-9]{64}$ ]] || fail 'encrypted_integrity_failed'
    printf '%s  %s\n' "$archive_sha256" 'backup.dump.enc' > "$encrypted_checksum"
    chmod 0600 "$encrypted_checksum"
    printf '{"backupId":"%s","bytes":%s,"sha256":"%s","dumpSha256":"%s","encryptedSha256":"%s","encryptionAlgorithm":"AES-256-CBC-PBKDF2","encryptionPolicy":"%s","retentionPolicy":"%s","retentionDays":%s,"verification":"pg_restore_list"}\n' \
        "$backup_id" "$archive_bytes" "$archive_sha256" "$raw_sha256" "$archive_sha256" "$encryption_policy" "$retention_policy" "$retention_days" > "$manifest"
    chmod 0600 "$manifest"
else
    archive_bytes="$raw_bytes"
    archive_sha256="$raw_sha256"
    printf '%s  %s\n' "$archive_sha256" 'backup.dump' > "$checksum"
    chmod 0600 "$checksum"
    printf '{"backupId":"%s","bytes":%s,"sha256":"%s","encryptionAlgorithm":"NONE","encryptionPolicy":"%s","retentionPolicy":"%s","retentionDays":%s,"verification":"pg_restore_list"}\n' \
        "$backup_id" "$archive_bytes" "$archive_sha256" "$encryption_policy" "$retention_policy" "$retention_days" > "$manifest"
    chmod 0600 "$manifest"
fi

if ! mv -T -n -- "$staging_directory" "$published_directory"; then
    fail 'publish_failed'
fi
if [[ -d "$staging_directory" ]]; then
    fail 'publish_collision'
fi
staging_directory=''

if [[ "$retention_policy" == 'prune-after-verified-7d' ]]; then
    while IFS= read -r expired_dir; do
        [[ -n "$expired_dir" && "$expired_dir" != "$published_directory" && ! -L "$expired_dir" ]] || continue
        rm -rf -- "$expired_dir"
    done < <(find "$output_directory" -mindepth 1 -maxdepth 1 -type d -name 'postgres-*' -mtime "+$retention_days" 2>/dev/null || true)
fi

log_event 'succeeded' "$archive_bytes" 'pg_restore_list' 'none'
