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

done_fixture="$fixture_dir/done.yaml"
printf '%s\n' \
    'schema_version: 2' \
    'epics:' \
    '  - id: E-00' \
    '    stories:' \
    '      - id: US-001' \
    '        status: Done' \
    '        code: [scripts/validate-traceability.sh]' \
    '        tests: [scripts/test-traceability-validator.sh]' \
    '        command: ./scripts/test-traceability-validator.sh' \
    '        observed_result: Traceability validator contract passed' \
    '        review: [docs/reviews/2026-09-19-risk-execution-review.md]' \
    '        evidence: [plan/traceability.yaml]' \
    '        residual_risk: [docs/production-readiness-audit-2026-10-07.md]' \
    '        rollback: [docs/runbook-local.md]' >"$done_fixture"
"$validator" "$done_fixture" >"$fixture_dir/done.out" 2>&1
grep -q '1 Done items with complete local evidence links' "$fixture_dir/done.out"

missing_done_link_fixture="$fixture_dir/missing-done-link.yaml"
sed '/        review:/d' "$done_fixture" >"$missing_done_link_fixture"
if "$validator" "$missing_done_link_fixture" >"$fixture_dir/missing-done-link.out" 2>&1; then
    echo "Validator contract failed: Done item without a review link was accepted." >&2
    exit 1
fi
grep -q 'Done item US-001 is missing required link: review' "$fixture_dir/missing-done-link.out"

invalid_done_link_fixture="$fixture_dir/invalid-done-link.yaml"
sed 's#plan/traceability.yaml#tests/traceability/missing-evidence.md#' "$done_fixture" >"$invalid_done_link_fixture"
if "$validator" "$invalid_done_link_fixture" >"$fixture_dir/invalid-done-link.out" 2>&1; then
    echo "Validator contract failed: Done item with a missing evidence link was accepted." >&2
    exit 1
fi
grep -q 'Done item US-001 has a non-existent evidence link' "$fixture_dir/invalid-done-link.out"

echo "Traceability validator contract passed: planned, complete Done, duplicate, range, missing-link, and invalid-link cases behave as expected."
