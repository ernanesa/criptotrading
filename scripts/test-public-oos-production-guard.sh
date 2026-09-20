#!/usr/bin/env bash
set -Eeuo pipefail

output_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-public-oos-guard.XXXXXX")"
cleanup() {
    rm -f -- "$output_file"
}
trap cleanup EXIT

set +e
api_key_name='BINANCE_API_KEY'
api_secret_name='BINANCE_API_SECRET'
fixture_value='fixture-only'
env "$api_key_name=$fixture_value" "$api_secret_name=$fixture_value" \
    ./scripts/test-public-oos-production.sh >"$output_file" 2>&1
status=$?
set -e

if [[ "$status" -ne 2 ]]; then
    echo "Public OOS credential guard returned unexpected exit code: $status" >&2
    exit 1
fi

if ! grep -Fq 'Refusing public OOS run while Binance credentials are present' "$output_file"; then
    echo 'Public OOS credential guard did not emit its refusal reason.' >&2
    exit 1
fi

echo 'Public OOS credential guard passed: refusal occurred before network access.'
