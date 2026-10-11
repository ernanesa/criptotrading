#!/usr/bin/env bash
set -Eeuo pipefail

# Runs the verified backup CLI under a local lock. It deliberately does not
# install a scheduler, restore data, remove old artifacts, or encrypt data.

usage() {
    echo 'Usage: backup-postgres-scheduled.sh --container NAME --database NAME --user NAME --output-directory ABSOLUTE_PATH --min-free-bytes BYTES --lock-file ABSOLUTE_PATH --attempt-timeout-seconds SECONDS --retry-count COUNT --retry-delay-seconds SECONDS --encryption-policy local-private-unencrypted-approved --retention-policy no-automatic-deletion-approved' >&2
}

container=''
database=''
database_user=''
output_directory=''
min_free_bytes=''
lock_file=''
attempt_timeout_seconds=''
retry_count=''
retry_delay_seconds=''
encryption_policy=''
retention_policy=''
repo_root="$(cd -P "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
backup_script="$repo_root/scripts/backup-postgres.sh"

while (($#)); do
    case "$1" in
        --container|--database|--user|--output-directory|--min-free-bytes|--lock-file|--attempt-timeout-seconds|--retry-count|--retry-delay-seconds|--encryption-policy|--retention-policy)
            (($# >= 2)) || { usage; exit 2; }
            case "$1" in
                --container) container="$2" ;;
                --database) database="$2" ;;
                --user) database_user="$2" ;;
                --output-directory) output_directory="$2" ;;
                --min-free-bytes) min_free_bytes="$2" ;;
                --lock-file) lock_file="$2" ;;
                --attempt-timeout-seconds) attempt_timeout_seconds="$2" ;;
                --retry-count) retry_count="$2" ;;
                --retry-delay-seconds) retry_delay_seconds="$2" ;;
                --encryption-policy) encryption_policy="$2" ;;
                --retention-policy) retention_policy="$2" ;;
            esac
            shift 2
            ;;
        --help) usage; exit 0 ;;
        *) usage; exit 2 ;;
    esac
done

[[ -x "$backup_script" ]] || { echo 'Verified backup command is unavailable.' >&2; exit 2; }
[[ -n "$container" && -n "$database" && -n "$database_user" && -n "$output_directory" && -n "$min_free_bytes" && -n "$lock_file" && -n "$attempt_timeout_seconds" && -n "$retry_count" && -n "$retry_delay_seconds" ]] || { usage; exit 2; }
[[ "$output_directory" == /* && "$lock_file" == /* && "$min_free_bytes" =~ ^[0-9]+$ && "$attempt_timeout_seconds" =~ ^[1-9][0-9]*$ && "$retry_count" =~ ^[0-9]+$ && "$retry_delay_seconds" =~ ^[0-9]+$ ]] || { usage; exit 2; }
(( attempt_timeout_seconds <= 86400 && retry_count <= 10 && retry_delay_seconds <= 3600 )) || { usage; exit 2; }

if [[ "$encryption_policy" != 'local-private-unencrypted-approved' ]]; then
    echo 'A human-approved local encryption policy is required; this command does not implement encryption.' >&2
    exit 2
fi
if [[ "$retention_policy" != 'no-automatic-deletion-approved' ]]; then
    echo 'A human-approved retention policy is required; this command never deletes backup artifacts.' >&2
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
if [[ "$output_directory" == / || "$output_directory" == "$repo_root" || "$output_directory" == "$repo_root"/* || "$output_owner" != "$(id -u)" || ! "$output_mode" =~ ^[0-7]{3,4}$ ]] || (( 8#$output_mode & 077 )); then
    echo 'Backup output directory must be user-owned, private, outside the repository, and not the filesystem root.' >&2
    exit 2
fi
if ! command -v git >/dev/null 2>&1 || git -C "$output_directory" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    echo 'Backup output directory must be outside every Git worktree.' >&2
    exit 2
fi

lock_parent="$(dirname -- "$lock_file")"
[[ "$lock_parent" == "$output_directory" && "$(basename -- "$lock_file")" == '.backup-postgres-scheduled.lock' && ! -L "$lock_file" ]] || {
    echo 'Lock file must be the regular .backup-postgres-scheduled.lock file in the backup output directory.' >&2
    exit 2
}

umask 077
if [[ ! -e "$lock_file" ]]; then
    set -C
    if ! : > "$lock_file" 2>/dev/null; then
        set +C
        echo 'Unable to create the scheduled-backup lock file.' >&2
        exit 2
    fi
    set +C
    chmod 0600 "$lock_file"
fi
[[ -f "$lock_file" && ! -L "$lock_file" && "$(stat -c '%u:%a' -- "$lock_file")" == "$(id -u):600" ]] || {
    echo 'Scheduled-backup lock file must be a user-owned regular file with mode 0600.' >&2
    exit 2
}
command -v flock >/dev/null 2>&1 || { echo 'flock is required for scheduled backups.' >&2; exit 2; }
command -v timeout >/dev/null 2>&1 || { echo 'timeout is required for scheduled backups.' >&2; exit 2; }

started_epoch="$(date +%s)"
run_id="postgres-scheduled-$(date -u +%Y%m%dT%H%M%SZ)-$$"
max_attempts=$((retry_count + 1))
log_event() {
    local status="$1" attempt="$2" reason="$3"
    local elapsed_seconds=$(( $(date +%s) - started_epoch ))
    printf '{"timestamp":"%s","status":"%s","runId":"%s","attempt":%s,"maxAttempts":%s,"durationSeconds":%s,"reason":"%s"}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$status" "$run_id" "$attempt" "$max_attempts" "$elapsed_seconds" "$reason" >&2
}

exec {lock_fd}<>"$lock_file"
if ! flock -n "$lock_fd"; then
    log_event 'failed' 0 'lock_unavailable'
    exit 1
fi

attempt_log="$(mktemp "$output_directory/.backup-postgres-scheduled.XXXXXX")"
chmod 0600 "$attempt_log"
trap 'rm -f -- "$attempt_log"' EXIT

for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    if timeout --signal=TERM --kill-after=5 "$attempt_timeout_seconds" "$backup_script" \
        --container "$container" --database "$database" --user "$database_user" \
        --output-directory "$output_directory" --min-free-bytes "$min_free_bytes" \
        > /dev/null 2>"$attempt_log"; then
        log_event 'succeeded' "$attempt" 'none'
        exit 0
    else
        exit_code=$?
    fi

    if (( exit_code == 124 )); then
        reason='attempt_timed_out'
    else
        reason='backup_command_failed'
    fi
    if (( attempt == max_attempts )); then
        log_event 'failed' "$attempt" "$reason"
        exit 1
    fi
    log_event 'retrying' "$attempt" "$reason"
    sleep "$retry_delay_seconds"
done
