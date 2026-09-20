#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
helper="$repo_root/scripts/init-testnet-secrets.sh"
tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-secret-init.XXXXXX")"
cleanup() { rm -rf -- "$tmp_dir"; }
trap cleanup EXIT

chmod 700 "$tmp_dir"
key_value="generated-$(date +%s%N)"
secret_value="generated-$(date +%s%N)"
output_file="$tmp_dir/output"
printf '%s\n%s\n' "$key_value" "$secret_value" |
    XDG_CONFIG_HOME="$tmp_dir" "$helper" >"$output_file"

secret_root="$tmp_dir/cryptotrading/testnet"
[[ "$(stat -c '%a' "$secret_root")" == "700" ]] || { echo 'Secret directory mode is not 700.' >&2; exit 1; }
for file in "$secret_root/binance_api_key" "$secret_root/binance_api_secret"; do
    [[ "$(stat -c '%a' "$file")" == "600" ]] || { echo 'Secret file mode is not 600.' >&2; exit 1; }
    [[ ! -L "$file" ]] || { echo 'Secret file is a symlink.' >&2; exit 1; }
done
if grep -Fq "$key_value" "$output_file" || grep -Fq "$secret_value" "$output_file"; then
    echo 'Secret initializer printed a credential value.' >&2
    exit 1
fi

rm -f -- "$secret_root/binance_api_key"
ln -s "$secret_root/binance_api_secret" "$secret_root/binance_api_key"
if printf 'generated-key\ngenerated-secret\n' |
    XDG_CONFIG_HOME="$tmp_dir" "$helper" >/dev/null 2>&1; then
    echo 'Secret initializer accepted a symlinked destination file.' >&2
    exit 1
fi

symlink_root="$tmp_dir/symlink-case"
mkdir -p "$symlink_root/target" "$symlink_root/cryptotrading"
ln -s "$symlink_root/target" "$symlink_root/cryptotrading/testnet"
if printf 'generated-key\ngenerated-secret\n' |
    XDG_CONFIG_HOME="$symlink_root" "$helper" >/dev/null 2>&1; then
    echo 'Secret initializer accepted a symlinked root.' >&2
    exit 1
fi

echo 'Secret initializer contract passed: private modes, redaction and symlink rejection verified.'
