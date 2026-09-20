#!/usr/bin/env bash
set -Eeuo pipefail

output_file="$(mktemp "${TMPDIR:-/tmp}/cryptotrading-public-oos-guard.XXXXXX")"
cleanup() {
    rm -f -- "$output_file"
}
trap cleanup EXIT

set +e
BINANCE_API_KEY=fixture-only BINANCE_API_SECRET=fixture-only \
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
