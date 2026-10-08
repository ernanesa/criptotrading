#!/usr/bin/env bash
set -Eeuo pipefail

# Creates a local Docker Compose secret without writing its value to stdout,
# shell history, the repository, or a container image.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
secret_dir="${API_INTERNAL_ACCESS_TOKEN_DIRECTORY:-$repo_root/.local-secrets}"
secret_file="$secret_dir/api_internal_access_token"
expected_uid="${LOCAL_UID:-$(id -u)}"

if [[ "$(id -u)" != "$expected_uid" ]]; then
    echo 'LOCAL_UID must match the user creating the proxy secret.' >&2
    exit 2
fi

umask 077
install -d -m 0700 "$secret_dir"
if [[ -L "$secret_file" ]]; then
    echo 'Refusing to replace a symlinked proxy-secret path.' >&2
    exit 2
fi

temporary_file="$(mktemp "$secret_dir/.api_internal_access_token.XXXXXX")"
trap 'rm -f -- "$temporary_file"' EXIT
openssl rand -hex 32 > "$temporary_file"
chmod 0600 "$temporary_file"
mv -f -- "$temporary_file" "$secret_file"
trap - EXIT

echo "Proxy secret initialized at $secret_file (uid $expected_uid, mode 0600)."
echo "Export API_INTERNAL_ACCESS_TOKEN_SOURCE=$secret_file before starting API/Web."
