#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-readonly-diagnostics.XXXXXX")"
trap 'rm -rf -- "$temp_dir"' EXIT
mkdir -m 700 -- "$temp_dir/reports"
fake_probe="$temp_dir/fake-probe"
cat >"$fake_probe" <<'SH'
#!/usr/bin/env bash
printf '%s\n' 'probe password=DIAGNOSTIC_PASSWORD_SENTINEL token=DIAGNOSTIC_TOKEN_SENTINEL'
printf '%s\n' 'db=postgresql://user:DIAGNOSTIC_URI_SENTINEL@localhost/db'
python3 - <<'PY'
print('oversized=' + ('x' * 20000) + 'DIAGNOSTIC_LONG_TAIL_SENTINEL')
PY
printf '%s\n' 'diagnostic benign marker: expected probe fixture failed'
exit 17
SH
chmod 700 "$fake_probe"

set +e
output="$(PREFLIGHT_TEST_MODE=1 \
    PREFLIGHT_TEST_EXECUTABLE="$fake_probe" \
    PREFLIGHT_TEST_REPORT_DIR="$temp_dir/reports" \
    WORKER_NO_TRADE=true \
    WORKER_EXECUTION_MODE=PAPER \
    "$repo_root/scripts/test-binance-readonly-preflight.sh" 2>&1)"
status=$?
set -e

if (( status != 1 )) || [[ "$output" != *'Sanitized diagnostics:'* ]]; then
    echo 'Read-only preflight did not preserve diagnostics on the expected failure path.' >&2
    exit 1
fi

diagnostics_file="$(find "$temp_dir/reports" -name probe.log -type f -print -quit)"
[[ -n "$diagnostics_file" ]] || {
    echo 'Sanitized diagnostics file was not created.' >&2
    exit 1
}
python3 - "$diagnostics_file" <<'PY'
import sys

payload = open(sys.argv[1], "rb").read()
for secret in (
    b"DIAGNOSTIC_PASSWORD_SENTINEL",
    b"DIAGNOSTIC_TOKEN_SENTINEL",
    b"DIAGNOSTIC_URI_SENTINEL",
    b"DIAGNOSTIC_LONG_TAIL_SENTINEL",
):
    if secret in payload:
        raise SystemExit("preflight diagnostics leaked a synthetic secret marker")
for expected in (
    b"[REDACTED]",
    b"[REDACTED: oversized diagnostic line omitted]",
    b"diagnostic benign marker",
):
    if expected not in payload:
        raise SystemExit("preflight diagnostics omitted expected sanitized evidence")
if len(payload) > 16_384 or len(payload.splitlines()) > 200:
    raise SystemExit("preflight diagnostics exceeded output bounds")
PY
[[ "$(stat -c '%a' -- "$diagnostics_file")" == '600' ]]

echo 'Read-only preflight diagnostics contract passed: failure evidence is bounded, redacted and private.'
