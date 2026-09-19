#!/usr/bin/env bash
set -Eeuo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
validator="$repo_root/scripts/validate-readiness.sh"
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-readiness.XXXXXX")"
cleanup() { rm -rf -- "$fixture_dir"; }
trap cleanup EXIT
prm="$fixture_dir/prm.md"; canary="$fixture_dir/canary.md"; risk="$fixture_dir/risk.md"
printf '%s\n' '# PRM' 'Decision: Go' 'Owner/reviewer/approver: owner reviewer approver' 'Valid until: 2099-01-01' 'Threshold: 2%; Units: percent; Window: 30d' >"$prm"
printf '%s\n' '# Canary' 'Status: Approved' 'Owner/approver: owner approver' 'Valid until: 2099-01-01' 'Capital/notional: 0 USDT' >"$canary"
printf '%s\n' 'Version: risk-v1' 'Hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef' 'Approved by: approver' 'Valid until: 2099-01-01' 'Threshold daily-loss: 2%' >"$risk"

"$validator" "$prm" "$canary" "$risk"
printf '%s\n' '- [ ] missing approval' >>"$canary"
if "$validator" "$prm" "$canary" "$risk" >"$fixture_dir/invalid.out" 2>&1; then
    echo "Readiness validator contract failed: unchecked fixture was accepted." >&2
    exit 1
fi
grep -q 'unchecked mandatory item' "$fixture_dir/invalid.out"

cp "$fixture_dir/prm.md" "$fixture_dir/expired-prm.md"
sed -i 's/2099-01-01/2020-01-01/' "$fixture_dir/expired-prm.md"
if READINESS_AS_OF_UTC=2026-09-19 "$validator" "$fixture_dir/expired-prm.md" "$fixture_dir/canary.md" "$fixture_dir/risk.md" >"$fixture_dir/expired.out" 2>&1; then
    echo "Readiness validator contract failed: expired PRM was accepted." >&2
    exit 1
fi
grep -q 'is expired as of 2026-09-19' "$fixture_dir/expired.out"

cp "$fixture_dir/risk.md" "$fixture_dir/invalid-date-risk.md"
sed -i 's/2099-01-01/2026-02-30/' "$fixture_dir/invalid-date-risk.md"
if READINESS_AS_OF_UTC=2026-09-19 "$validator" "$fixture_dir/prm.md" "$fixture_dir/canary.md" "$fixture_dir/invalid-date-risk.md" >"$fixture_dir/invalid-date.out" 2>&1; then
    echo "Readiness validator contract failed: invalid RiskPolicy date was accepted." >&2
    exit 1
fi
grep -q 'is not a calendar date' "$fixture_dir/invalid-date.out"

cp "$fixture_dir/canary.md" "$fixture_dir/expired-canary.md"
sed -i 's/2099-01-01/2020-01-01/' "$fixture_dir/expired-canary.md"
if READINESS_AS_OF_UTC=2026-09-19 "$validator" "$fixture_dir/prm.md" "$fixture_dir/expired-canary.md" "$fixture_dir/risk.md" >"$fixture_dir/expired-canary.out" 2>&1; then
    echo "Readiness validator contract failed: expired CanaryPlan was accepted." >&2
    exit 1
fi
grep -q 'expired-canary.md: (Válida até|Valid until) is expired as of 2026-09-19' "$fixture_dir/expired-canary.out"

cp "$fixture_dir/canary.md" "$fixture_dir/invalid-date-canary.md"
sed -i 's/2099-01-01/2026-02-30/' "$fixture_dir/invalid-date-canary.md"
if READINESS_AS_OF_UTC=2026-09-19 "$validator" "$fixture_dir/prm.md" "$fixture_dir/invalid-date-canary.md" "$fixture_dir/risk.md" >"$fixture_dir/invalid-date-canary.out" 2>&1; then
    echo "Readiness validator contract failed: invalid CanaryPlan date was accepted." >&2
    exit 1
fi
grep -q 'is not a calendar date' "$fixture_dir/invalid-date-canary.out"

echo "Readiness validator contract passed: complete and incomplete artifacts behave as expected."
