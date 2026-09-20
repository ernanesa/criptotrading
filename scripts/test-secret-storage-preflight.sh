#!/usr/bin/env bash
set -Eeuo pipefail

# Offline contract test for the Testnet secret-storage preflight. Values are
# generated at runtime so this script never introduces credential-shaped
# literals into the repository or sends anything to an exchange.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
preflight="$repo_root/scripts/check-testnet-secret-storage.sh"
tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-secret-preflight.XXXXXX")"
cleanup() {
    rm -f -- "$tmp_dir/api-key" "$tmp_dir/api-secret" "$tmp_dir/api-key-link"
    rmdir -- "$tmp_dir" 2>/dev/null || true
}
trap cleanup EXIT

export TRADING_ENVIRONMENT=TESTNET
export BINANCE_REST_BASE_URL=https://testnet.binance.vision/api
export BINANCE_WITHDRAWALS_ENABLED=false

key_variable=BINANCE_API_KEY
secret_variable=BINANCE_API_SECRET
key_file_variable=BINANCE_API_KEY_FILE
secret_file_variable=BINANCE_API_SECRET_FILE
export "$key_variable=generated-$(date +%s%N)"
export "$secret_variable=generated-$(date +%s%N)"
unset "$key_file_variable" "$secret_file_variable"
"$preflight" >/dev/null

unset "$key_variable" "$secret_variable"
printf 'generated-%s' "$(date +%s%N)" >"$tmp_dir/api-key"
printf 'generated-%s' "$(date +%s%N)" >"$tmp_dir/api-secret"
chmod 600 "$tmp_dir/api-key" "$tmp_dir/api-secret"
export "$key_file_variable=$tmp_dir/api-key"
export "$secret_file_variable=$tmp_dir/api-secret"
"$preflight" >/dev/null

chmod 640 "$tmp_dir/api-key"
if "$preflight" >/dev/null 2>&1; then
    echo 'Secret preflight accepted a group-readable credential file.' >&2
    exit 1
fi
chmod 600 "$tmp_dir/api-key"

ln -s "$tmp_dir/api-key" "$tmp_dir/api-key-link"
export "$key_file_variable=$tmp_dir/api-key-link"
if "$preflight" >/dev/null 2>&1; then
    echo 'Secret preflight accepted a credential symlink.' >&2
    exit 1
fi

echo 'Secret storage preflight contract passed: direct, 0600 file, insecure mode and symlink cases verified.'
