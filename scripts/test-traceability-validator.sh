#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
validator="$repo_root/scripts/validate-traceability.sh"
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-traceability.XXXXXX")"
cleanup() {
    rm -rf -- "$fixture_dir"
}
trap cleanup EXIT

"$validator"

duplicate_fixture="$fixture_dir/duplicate.yaml"
printf '%s\n' \
    'schema_version: 2' \
    'epics:' \
    '  - id: E-00' \
    '    stories:' \
    '      - id: US-001' \
    '      - id: US-001' >"$duplicate_fixture"
if "$validator" "$duplicate_fixture" >"$fixture_dir/duplicate.out" 2>&1; then
    echo "Validator contract failed: duplicate fixture was accepted." >&2
    exit 1
fi
grep -q 'duplicate IDs: US-001' "$fixture_dir/duplicate.out"

range_fixture="$fixture_dir/range.yaml"
printf '%s\n' \
    'schema_version: 2' \
    'epics:' \
    '  - id: E-00' \
    '    stories:' \
    '      - id: US-001..003' >"$range_fixture"
if "$validator" "$range_fixture" >"$fixture_dir/range.out" 2>&1; then
    echo "Validator contract failed: implicit range fixture was accepted." >&2
    exit 1
fi
grep -q 'implicit numeric ranges are forbidden' "$fixture_dir/range.out"

echo "Traceability validator contract passed: valid, duplicate, and range cases behave as expected."
