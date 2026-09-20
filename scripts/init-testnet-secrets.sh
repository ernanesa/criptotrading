#!/usr/bin/env bash
set -euo pipefail

secret_root="${XDG_CONFIG_HOME:-/home/ernane/.config}/cryptotrading/testnet"
umask 077
[[ "$secret_root" == /* ]] || { echo 'Secret root must be an absolute path.' >&2; exit 2; }
if [[ -L "$secret_root" ]]; then
  echo 'Secret root must not be a symbolic link.' >&2
  exit 2
fi
if [[ -e "$secret_root" && ! -d "$secret_root" ]]; then
  echo 'Secret root must be a directory.' >&2
  exit 2
fi
mkdir -p "$secret_root"
chmod 700 "$secret_root"

read -r -p 'Binance Spot Testnet API key (input hidden): ' api_key
printf '\n'
read -r -s -p 'Binance Spot Testnet secret key (input hidden): ' api_secret
printf '\n'

if [[ -z "$api_key" || -z "$api_secret" ]]; then
  echo 'Both values are required; no files were written.' >&2
  exit 1
fi

for secret_file in "$secret_root/binance_api_key" "$secret_root/binance_api_secret"; do
  if [[ -L "$secret_file" ]]; then
    echo "Refusing to overwrite symbolic link: $secret_file" >&2
    exit 2
  fi
done

key_tmp="$(mktemp "$secret_root/.binance_api_key.XXXXXX")"
secret_tmp="$(mktemp "$secret_root/.binance_api_secret.XXXXXX")"
trap 'rm -f -- "$key_tmp" "$secret_tmp"' EXIT
printf '%s' "$api_key" > "$key_tmp"
printf '%s' "$api_secret" > "$secret_tmp"
chmod 600 "$key_tmp" "$secret_tmp"
mv -f -- "$key_tmp" "$secret_root/binance_api_key"
mv -f -- "$secret_tmp" "$secret_root/binance_api_secret"
trap - EXIT

unset api_key api_secret
echo "Secrets stored in $secret_root with directory mode 700 and file mode 600."
echo 'Do not print, commit, upload or paste these values.'
