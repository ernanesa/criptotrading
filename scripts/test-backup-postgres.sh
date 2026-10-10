#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
backup_script="$repo_root/scripts/backup-postgres.sh"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-backup-contract.XXXXXX")"
fake_bin="$work_dir/bin"
mkdir -m 700 "$fake_bin"
trap 'rm -rf -- "$work_dir"' EXIT

cat > "$fake_bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$*" >> "${FAKE_DOCKER_LOG:?}"
case "${FAKE_DOCKER_MODE:-success}:$*" in
    dump_failure:*pg_dump*) echo 'fixture-secret-dump-error' >&2; exit 17 ;;
    verify_failure:*pg_restore\ --list*) echo 'fixture-secret-verify-error' >&2; exit 19 ;;
    *:*pg_dump*) printf '%s' "${FAKE_DUMP_PAYLOAD:-fixture}" ;;
    *:*pg_restore\ --list*) cat >/dev/null ;;
    *) exit 23 ;;
esac
DOCKER
cat > "$fake_bin/df" <<'DF'
#!/usr/bin/env bash
printf 'Filesystem 1B-blocks Used Available Use%% Mounted on\nmock 100000000 0 %s 0%% /\n' "${FAKE_FREE_BYTES:?}"
DF
chmod 700 "$fake_bin/docker" "$fake_bin/df"
bash -n "$fake_bin/docker" "$fake_bin/df"

run_backup() {
    local directory="$1" mode="$2" minimum="$3"
    PATH="$fake_bin:$PATH" FAKE_DOCKER_LOG="$work_dir/docker.log" FAKE_DOCKER_MODE="$mode" FAKE_FREE_BYTES=1000000 \
        "$backup_script" --container postgres-fixture --database trading --user backup_user \
        --output-directory "$directory" --min-free-bytes "$minimum" >"$work_dir/stdout" 2>"$work_dir/stderr"
}

success_dir="$work_dir/success"
mkdir -m 700 "$success_dir"
run_backup "$success_dir" success 100
artifact_dir="$(find "$success_dir" -mindepth 1 -maxdepth 1 -type d -name 'postgres-*' -print -quit)"
[[ -n "$artifact_dir" && -s "$artifact_dir/backup.dump" && -s "$artifact_dir/backup.dump.sha256" && -s "$artifact_dir/manifest.json" ]]
[[ "$(stat -c '%a' "$artifact_dir")" == 700 ]]
for artifact_file in "$artifact_dir/backup.dump" "$artifact_dir/backup.dump.sha256" "$artifact_dir/manifest.json"; do
    [[ "$(stat -c '%a' "$artifact_file")" == 600 && ! -L "$artifact_file" ]]
done
(cd "$artifact_dir" && sha256sum --check backup.dump.sha256 >/dev/null)
grep -Fq 'pg_restore --list' "$work_dir/docker.log"
grep -Eq '"status":"succeeded".*"verification":"pg_restore_list"' "$work_dir/stderr"

failure_dir="$work_dir/dump-failure"
mkdir -m 700 "$failure_dir"
if run_backup "$failure_dir" dump_failure 100; then
    echo 'Dump failure unexpectedly succeeded.' >&2
    exit 1
fi
[[ -z "$(find "$failure_dir" -mindepth 1 -print -quit)" ]]
grep -Fq '"reason":"pg_dump_failed"' "$work_dir/stderr"

verify_dir="$work_dir/verify-failure"
mkdir -m 700 "$verify_dir"
if run_backup "$verify_dir" verify_failure 100; then
    echo 'Verification failure unexpectedly succeeded.' >&2
    exit 1
fi
[[ -z "$(find "$verify_dir" -mindepth 1 -print -quit)" ]]
grep -Fq '"reason":"pg_restore_verification_failed"' "$work_dir/stderr"

space_dir="$work_dir/space-failure"
mkdir -m 700 "$space_dir"
if run_backup "$space_dir" success 1000001; then
    echo 'Insufficient-space case unexpectedly succeeded.' >&2
    exit 1
fi
[[ -z "$(find "$space_dir" -mindepth 1 -print -quit)" ]]
grep -Fq '"reason":"insufficient_free_space"' "$work_dir/stderr"

symlink_target="$work_dir/symlink-target"
symlink_path="$work_dir/symlink-output"
mkdir -m 700 "$symlink_target"
ln -s "$symlink_target" "$symlink_path"
if run_backup "$symlink_path" success 100; then
    echo 'Symlinked output directory unexpectedly succeeded.' >&2
    exit 1
fi
[[ -z "$(find "$symlink_target" -mindepth 1 -print -quit)" ]]

insecure_dir="$work_dir/insecure-output"
mkdir -m 755 "$insecure_dir"
if run_backup "$insecure_dir" success 100; then
    echo 'Non-private output directory unexpectedly succeeded.' >&2
    exit 1
fi
grep -Fq 'user-owned, private' "$work_dir/stderr"

if run_backup "$repo_root" success 100; then
    echo 'Repository output directory unexpectedly succeeded.' >&2
    exit 1
fi
grep -Fq 'outside the repository' "$work_dir/stderr"

if grep -Eq 'fixture|backup_user|trading' "$work_dir/stderr"; then
    echo 'Backup logs exposed a synthetic payload or connection identifier.' >&2
    exit 1
fi
echo 'PostgreSQL backup contract passed: private artifact, verification, failure cleanup, space and symlink checks.'
