#!/usr/bin/env bash
set -Eeuo pipefail

# Deterministically exercise the recovery runner's failure-log path without
# starting Docker or invoking any of the recovery child commands.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-recovery-diagnostics.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT
report_dir="$work_dir/reports"
diagnostics_dir="$work_dir/diagnostics"

status=0
RECOVERY_REPORT_DIR="$report_dir" \
RECOVERY_DIAGNOSTICS_DIR="$diagnostics_dir" \
    "$repo_root/scripts/test-full-recovery-drill.sh" --self-test-failure-path >"$work_dir/runner-output.log" 2>&1 || status=$?

if (( status == 0 )); then
    echo "Recovery diagnostics failure-path probe unexpectedly passed." >&2
    exit 1
fi

diagnostic_file="$diagnostics_dir/diagnostics-fixture.log"
report_file="$(find "$report_dir" -maxdepth 1 -type f -name 'full-recovery-*.md' -print -quit)"
[[ -s "$diagnostic_file" && -n "$report_file" ]] || {
    echo "Recovery failure did not preserve the expected report and diagnostics." >&2
    exit 1
}

if grep -Eq 'TEST_ONLY_SENTINEL|FAKE_SECRET|JSON_SECRET|URI_SECRET|LONG_TAIL_SECRET' "$diagnostic_file"; then
    echo "Recovery diagnostics leaked the synthetic credential marker." >&2
    exit 1
fi
grep -q '\[REDACTED\]' "$diagnostic_file"
grep -q 'postgresql://user:\[REDACTED\]@db:5432/trades' "$diagnostic_file"
grep -q '\[REDACTED: oversized diagnostic line omitted\]' "$diagnostic_file"
grep -q 'A marker line without credentials\.' "$diagnostic_file"
[[ "$(wc -c < "$diagnostic_file")" -le 16384 ]]
[[ "$(wc -l < "$diagnostic_file")" -le 200 ]]
grep -q 'sanitized diagnostics' "$report_file"
[[ "$(stat -c '%a' "$diagnostics_dir")" == 700 ]]
[[ "$(stat -c '%a' "$diagnostic_file")" == 600 ]]

mkdir -m 700 "$work_dir/diagnostics-target"
ln -s "$work_dir/diagnostics-target" "$work_dir/diagnostics-symlink"
symlink_status=0
RECOVERY_REPORT_DIR="$work_dir/reports-symlink-check" \
RECOVERY_DIAGNOSTICS_DIR="$work_dir/diagnostics-symlink" \
    "$repo_root/scripts/test-full-recovery-drill.sh" --self-test-failure-path >"$work_dir/symlink-output.log" 2>&1 || symlink_status=$?
[[ "$symlink_status" == 2 ]]
[[ -z "$(find "$work_dir/diagnostics-target" -mindepth 1 -print -quit)" ]]

echo "Recovery failure diagnostics were redacted, bounded, permission-restricted, and preserved without Docker."
