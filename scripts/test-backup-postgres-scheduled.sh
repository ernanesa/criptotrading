#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scheduled_script="$repo_root/scripts/backup-postgres-scheduled.sh"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-backup-scheduled-contract.XXXXXX")"
fake_bin="$work_dir/bin"
mkdir -m 700 "$fake_bin"
trap 'rm -rf -- "$work_dir"' EXIT

cat > "$fake_bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$*" >> "${FAKE_DOCKER_LOG:?}"
case "${FAKE_DOCKER_MODE:-success}:$*" in
    fail_once:*pg_dump*)
        if [[ ! -e "${FAKE_DOCKER_COUNTER:?}" ]]; then
            : > "${FAKE_DOCKER_COUNTER:?}"
            exit 17
        fi
        printf '%s' fixture
        ;;
    always_fail:*pg_dump*) exit 17 ;;
    slow_dump:*pg_dump*) sleep 2; printf '%s' fixture ;;
    *:*pg_dump*) printf '%s' fixture ;;
    *:*pg_restore\ --list*) cat >/dev/null ;;
    *) exit 23 ;;
esac
DOCKER
cat > "$fake_bin/df" <<'DF'
#!/usr/bin/env bash
printf 'Filesystem 1B-blocks Used Available Use%% Mounted on\nmock 100000000 0 1000000 0%% /\n'
DF
chmod 700 "$fake_bin/docker" "$fake_bin/df"
bash -n "$fake_bin/docker" "$fake_bin/df" "$scheduled_script"

run_scheduled() {
    local directory="$1" mode="$2" timeout_seconds="$3" retries="$4"
    PATH="$fake_bin:$PATH" FAKE_DOCKER_LOG="$work_dir/docker.log" FAKE_DOCKER_MODE="$mode" FAKE_DOCKER_COUNTER="$work_dir/counter" \
        "$scheduled_script" --container postgres-fixture --database trading --user backup_user \
        --output-directory "$directory" --min-free-bytes 100 \
        --lock-file "$directory/.backup-postgres-scheduled.lock" \
        --attempt-timeout-seconds "$timeout_seconds" --retry-count "$retries" --retry-delay-seconds 0 \
        --encryption-policy local-private-unencrypted-approved \
        --retention-policy no-automatic-deletion-approved >"$work_dir/stdout" 2>"$work_dir/stderr"
}

success_dir="$work_dir/success"
mkdir -m 700 "$success_dir"
run_scheduled "$success_dir" success 5 0
artifact_dir="$(find "$success_dir" -mindepth 1 -maxdepth 1 -type d -name 'postgres-*' -print -quit)"
[[ -n "$artifact_dir" && -s "$artifact_dir/backup.dump" ]]
[[ "$(stat -c '%a' "$success_dir/.backup-postgres-scheduled.lock")" == 600 ]]
grep -Eq '"status":"succeeded".*"attempt":1.*"reason":"none"' "$work_dir/stderr"
if grep -Eq 'fixture|postgres-fixture|backup_user|trading' "$work_dir/stderr"; then
    echo 'Scheduled-backup logs exposed a synthetic payload or connection identifier.' >&2
    exit 1
fi

rm -f -- "$work_dir/counter"
retry_dir="$work_dir/retry"
mkdir -m 700 "$retry_dir"
run_scheduled "$retry_dir" fail_once 5 1
grep -Eq '"status":"retrying".*"reason":"backup_command_failed"' "$work_dir/stderr"
grep -Eq '"status":"succeeded".*"attempt":2' "$work_dir/stderr"

timeout_dir="$work_dir/timeout"
mkdir -m 700 "$timeout_dir"
if run_scheduled "$timeout_dir" slow_dump 1 0; then
    echo 'Timed-out backup unexpectedly succeeded.' >&2
    exit 1
fi
[[ -z "$(find "$timeout_dir" -mindepth 1 -type d -name 'postgres-*' -print -quit)" ]]
grep -Eq '"status":"failed".*"reason":"attempt_timed_out"' "$work_dir/stderr"

policy_dir="$work_dir/policy"
mkdir -m 700 "$policy_dir"
if PATH="$fake_bin:$PATH" "$scheduled_script" --container postgres-fixture --database trading --user backup_user \
    --output-directory "$policy_dir" --min-free-bytes 100 --lock-file "$policy_dir/.backup-postgres-scheduled.lock" \
    --attempt-timeout-seconds 5 --retry-count 0 --retry-delay-seconds 0 \
    --retention-policy no-automatic-deletion-approved >"$work_dir/stdout" 2>"$work_dir/stderr"; then
    echo 'Missing encryption-policy acknowledgement unexpectedly succeeded.' >&2
    exit 1
fi
grep -Fq 'human-approved local encryption policy' "$work_dir/stderr"

if PATH="$fake_bin:$PATH" "$scheduled_script" --container postgres-fixture --database trading --user backup_user \
    --output-directory "$policy_dir" --min-free-bytes 100 --lock-file "$policy_dir/.backup-postgres-scheduled.lock" \
    --attempt-timeout-seconds 5 --retry-count 0 --retry-delay-seconds 0 \
    --encryption-policy local-private-unencrypted-approved >"$work_dir/stdout" 2>"$work_dir/stderr"; then
    echo 'Missing retention-policy acknowledgement unexpectedly succeeded.' >&2
    exit 1
fi
grep -Fq 'human-approved retention policy' "$work_dir/stderr"

lock_dir="$work_dir/lock"
mkdir -m 700 "$lock_dir"
PATH="$fake_bin:$PATH" FAKE_DOCKER_LOG="$work_dir/docker.log" FAKE_DOCKER_MODE=slow_dump FAKE_DOCKER_COUNTER="$work_dir/counter" \
    "$scheduled_script" --container postgres-fixture --database trading --user backup_user \
    --output-directory "$lock_dir" --min-free-bytes 100 --lock-file "$lock_dir/.backup-postgres-scheduled.lock" \
    --attempt-timeout-seconds 5 --retry-count 0 --retry-delay-seconds 0 \
    --encryption-policy local-private-unencrypted-approved --retention-policy no-automatic-deletion-approved \
    >"$work_dir/lock-stdout" 2>"$work_dir/lock-first-stderr" &
lock_pid=$!
sleep 1
if run_scheduled "$lock_dir" success 5 0; then
    echo 'Concurrent scheduled backup unexpectedly acquired the lock.' >&2
    exit 1
fi
grep -Eq '"status":"failed".*"reason":"lock_unavailable"' "$work_dir/stderr"
wait "$lock_pid"

enc_sched_dir="$work_dir/enc-sched"
key_dir="$work_dir/keys"
mkdir -m 700 "$enc_sched_dir" "$key_dir"
key_file="$key_dir/postgres_backup.key"
printf 'scheduled-backup-key-material-32bytes' > "$key_file"
chmod 0600 "$key_file"
PATH="$fake_bin:$PATH" FAKE_DOCKER_LOG="$work_dir/docker.log" FAKE_DOCKER_MODE=success FAKE_DOCKER_COUNTER="$work_dir/counter" \
    "$scheduled_script" --container postgres-fixture --database trading --user backup_user \
    --output-directory "$enc_sched_dir" --min-free-bytes 100 \
    --lock-file "$enc_sched_dir/.backup-postgres-scheduled.lock" \
    --attempt-timeout-seconds 5 --retry-count 0 --retry-delay-seconds 0 \
    --encryption-policy aes256-keyfile --encryption-key-file "$key_file" \
    --retention-policy prune-after-verified-7d --retention-days 7 >"$work_dir/stdout" 2>"$work_dir/stderr"
grep -Eq '"status":"succeeded".*"rpoTargetSeconds":900,"rtoTargetSeconds":600,"encryptionPolicy":"aes256-keyfile","retentionPolicy":"prune-after-verified-7d"' "$work_dir/stderr"

echo 'Scheduled PostgreSQL backup contract passed: lock, timeout, bounded retry, AES-256/retention policy acknowledgements, and sanitized JSON events.'
