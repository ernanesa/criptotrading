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

export BINANCE_API_KEY="generated-$(date +%s%N)"
export BINANCE_API_SECRET="generated-$(date +%s%N)"
unset BINANCE_API_KEY_FILE BINANCE_API_SECRET_FILE
"$preflight" >/dev/null

unset BINANCE_API_KEY BINANCE_API_SECRET
printf 'generated-%s' "$(date +%s%N)" >"$tmp_dir/api-key"
printf 'generated-%s' "$(date +%s%N)" >"$tmp_dir/api-secret"
chmod 600 "$tmp_dir/api-key" "$tmp_dir/api-secret"
export BINANCE_API_KEY_FILE="$tmp_dir/api-key"
export BINANCE_API_SECRET_FILE="$tmp_dir/api-secret"
"$preflight" >/dev/null

chmod 640 "$tmp_dir/api-key"
if "$preflight" >/dev/null 2>&1; then
    echo 'Secret preflight accepted a group-readable credential file.' >&2
    exit 1
fi
chmod 600 "$tmp_dir/api-key"

ln -s "$tmp_dir/api-key" "$tmp_dir/api-key-link"
export BINANCE_API_KEY_FILE="$tmp_dir/api-key-link"
if "$preflight" >/dev/null 2>&1; then
    echo 'Secret preflight accepted a credential symlink.' >&2
    exit 1
fi

echo 'Secret storage preflight contract passed: direct, 0600 file, insecure mode and symlink cases verified.'
