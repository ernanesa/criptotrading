#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-secret-scan-tooling.XXXXXX")"
trap 'rm -rf -- "$temp_dir"' EXIT
mkdir -- "$temp_dir/bin"
for executable in bash git sort mktemp rm; do
    executable_path="$(command -v "$executable")"
    ln -s -- "$executable_path" "$temp_dir/bin/$executable"
done

set +e
output="$(PATH="$temp_dir/bin" "$repo_root/scripts/scan-secrets.sh" 2>&1)"
status=$?
set -e

if (( status != 127 )) || [[ "$output" != *'required ripgrep executable (rg) is missing'* ]]; then
    echo 'Secret scanner did not fail closed when ripgrep was unavailable.' >&2
    exit 1
fi

cat >"$temp_dir/bin/rg" <<'SH'
#!/usr/bin/env bash
exit 2
SH
chmod 700 "$temp_dir/bin/rg"
set +e
output="$(PATH="$temp_dir/bin" "$repo_root/scripts/scan-secrets.sh" 2>&1)"
status=$?
set -e
if (( status != 2 )) || [[ "$output" != *'Secret scan failed while scanning a repository file (rg exit 2)'* ]]; then
    echo 'Secret scanner treated a ripgrep execution error as an empty scan.' >&2
    exit 1
fi

echo 'Secret scanner tooling contract passed: missing ripgrep and scanner errors fail closed.'
