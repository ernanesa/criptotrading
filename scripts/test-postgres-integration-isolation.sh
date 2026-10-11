#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-pg-isolation.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT
mkdir -m 700 "$work_dir/bin" "$work_dir/dotnet" "$work_dir/locks"

cat > "$work_dir/bin/docker" <<'DOCKER'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$*" >> "${FAKE_DOCKER_LOG:?}"
case "$*" in
    *"ps --all --quiet"*)
        [[ "${FAKE_EXISTING_PROJECT:-false}" != true ]] || printf '%s\n' existing-container
        ;;
    "volume inspect "*)
        [[ "${FAKE_EXISTING_VOLUME:-false}" == true || -e "${FAKE_VOLUME_CREATED:?}" ]]
        ;;
    "volume ls --quiet")
        [[ "${FAKE_VOLUME_LIST_ERROR:-false}" != true ]] || exit 31
        [[ "${FAKE_EXISTING_VOLUME:-false}" != true ]] || printf '%s\n' isolation-fixture-volume
        ;;
    *"up -d postgres"*) : > "${FAKE_VOLUME_CREATED:?}" ;;
    *"pg_isready"*) exit 0 ;;
    *"down --remove-orphans"*) exit 0 ;;
    *) exit 27 ;;
esac
DOCKER
cat > "$work_dir/dotnet/dotnet" <<'DOTNET'
#!/usr/bin/env bash
set -Eeuo pipefail
[[ -n "${POSTGRES_CONNECTION_STRING:-}" ]]
exit "${FAKE_TEST_EXIT:-0}"
DOTNET
chmod 700 "$work_dir/bin/docker" "$work_dir/dotnet/dotnet"

run_harness() {
    env -u POSTGRES_TEST_PASSWORD -u POSTGRES_CONNECTION_STRING \
        PATH="$work_dir/bin:$PATH" POSTGRES_INTEGRATION_DOTNET_COMMAND="$work_dir/dotnet/dotnet" \
        POSTGRES_DB="${FIXTURE_DB:-integration}" POSTGRES_USER=integration POSTGRES_TEST_PORT=55449 \
        POSTGRES_VOLUME_NAME=isolation-fixture-volume COMPOSE_PROJECT_NAME=isolation-fixture \
        API_INTERNAL_ACCESS_TOKEN_SOURCE=/dev/null \
        POSTGRES_INTEGRATION_LOCK_DIRECTORY="$work_dir/locks" \
        FAKE_DOCKER_LOG="$work_dir/docker.log" FAKE_VOLUME_CREATED="$work_dir/volume-created" \
        bash "$repo_root/scripts/test-postgres-integration.sh" >"$work_dir/stdout" 2>"$work_dir/stderr"
}

export FAKE_EXISTING_PROJECT=true
if run_harness; then
    echo 'Harness reused an existing Compose project.' >&2
    exit 1
fi
if rg -q 'up -d|down --remove-orphans' "$work_dir/docker.log"; then
    echo 'Harness mutated an existing Compose project.' >&2
    exit 1
fi
unset FAKE_EXISTING_PROJECT

: > "$work_dir/docker.log"
export FAKE_EXISTING_VOLUME=true
if run_harness; then
    echo 'Harness reused an existing volume with a new ephemeral password.' >&2
    exit 1
fi
if rg -q 'up -d|down --remove-orphans' "$work_dir/docker.log"; then
    echo 'Harness mutated resources after detecting a pre-existing volume.' >&2
    exit 1
fi
unset FAKE_EXISTING_VOLUME

: > "$work_dir/docker.log"
export FAKE_VOLUME_LIST_ERROR=true
if run_harness; then
    echo 'Harness continued despite a volume enumeration failure.' >&2
    exit 1
fi
if rg -q 'up -d|down --remove-orphans' "$work_dir/docker.log"; then
    echo 'Harness mutated resources without proven volume isolation.' >&2
    exit 1
fi
unset FAKE_VOLUME_LIST_ERROR

: > "$work_dir/docker.log"
exec {held_lock}>"$work_dir/locks/harness.lock"
chmod 600 "$work_dir/locks/harness.lock"
flock --exclusive --nonblock "$held_lock"
if run_harness; then
    echo 'Harness started while another integration runner held the lock.' >&2
    exit 1
fi
[[ ! -s "$work_dir/docker.log" ]]
exec {held_lock}>&-

: > "$work_dir/docker.log"
export FIXTURE_DB='integration;Username=other'
if run_harness; then
    echo 'Harness accepted connection metadata containing DSN settings.' >&2
    exit 1
fi
[[ ! -s "$work_dir/docker.log" ]]
unset FIXTURE_DB

: > "$work_dir/docker.log"
run_harness
rg -q 'up -d postgres' "$work_dir/docker.log"
rg -q 'down --remove-orphans' "$work_dir/docker.log"
if rg -q -- '--volumes|volume rm' "$work_dir/docker.log"; then
    echo 'Harness unexpectedly deleted a retained volume.' >&2
    exit 1
fi

rm -f -- "$work_dir/volume-created"
: > "$work_dir/docker.log"
export FAKE_TEST_EXIT=19
if run_harness; then
    echo 'Harness swallowed a failing test exit.' >&2
    exit 1
fi
rg -q 'down --remove-orphans' "$work_dir/docker.log"
unset FAKE_TEST_EXIT

echo 'PostgreSQL isolation contract passed: pre-existing resources untouched, test failures preserved, retained volume never deleted.'
