#!/usr/bin/env bash
set -Eeuo pipefail

# Creates one verified PostgreSQL logical-backup artifact. This tool never
# restores data, reads .env files, or selects a container/database implicitly.

usage() {
    echo 'Usage: backup-postgres.sh --container NAME --database NAME --user NAME --output-directory ABSOLUTE_PATH --min-free-bytes BYTES' >&2
}

container=''
database=''
database_user=''
output_directory=''
min_free_bytes=''
repo_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

while (($#)); do
    case "$1" in
        --container|--database|--user|--output-directory|--min-free-bytes)
            (($# >= 2)) || { usage; exit 2; }
            case "$1" in
                --container) container="$2" ;;
                --database) database="$2" ;;
                --user) database_user="$2" ;;
                --output-directory) output_directory="$2" ;;
                --min-free-bytes) min_free_bytes="$2" ;;
            esac
            shift 2
            ;;
        --help) usage; exit 0 ;;
        *) usage; exit 2 ;;
    esac
done

[[ -n "$container" && -n "$database" && -n "$database_user" && -n "$output_directory" && -n "$min_free_bytes" ]] || { usage; exit 2; }
[[ "$output_directory" == /* && "$min_free_bytes" =~ ^[0-9]+$ ]] || { usage; exit 2; }

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
umask 077
backup_id="postgres-$(date -u +%Y%m%dT%H%M%SZ)-$$"
started_epoch="$(date +%s)"
staging_directory=''
published_directory=''
failed=0

log_event() {
    local status="$1" bytes="$2" verification="$3" reason="$4"
    local elapsed_seconds=$(( $(date +%s) - started_epoch ))
    printf '{"timestamp":"%s","status":"%s","backupId":"%s","bytes":%s,"durationSeconds":%s,"verification":"%s","reason":"%s"}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$status" "$backup_id" "$bytes" "$elapsed_seconds" "$verification" "$reason" >&2
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

archive_bytes="$(wc -c < "$archive" | tr -d '[:space:]')"
archive_sha256="$(sha256sum "$archive" 2>/dev/null | awk '{ print $1 }')"
[[ "$archive_bytes" =~ ^[1-9][0-9]*$ && "$archive_sha256" =~ ^[a-f0-9]{64}$ ]] || fail 'archive_integrity_failed'
printf '%s  %s\n' "$archive_sha256" 'backup.dump' > "$checksum"
chmod 0600 "$checksum"
printf '{"backupId":"%s","bytes":%s,"sha256":"%s","verification":"pg_restore_list"}\n' \
    "$backup_id" "$archive_bytes" "$archive_sha256" > "$manifest"
chmod 0600 "$manifest"

if ! mv -T -n -- "$staging_directory" "$published_directory"; then
    fail 'publish_failed'
fi
if [[ -d "$staging_directory" ]]; then
    fail 'publish_collision'
fi
staging_directory=''
log_event 'succeeded' "$archive_bytes" 'pg_restore_list' 'none'
