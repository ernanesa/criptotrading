#!/usr/bin/env bash
set -euo pipefail

secret_root="${XDG_CONFIG_HOME:-/home/ernane/.config}/cryptotrading/testnet"
umask 077
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

printf '%s' "$api_key" > "$secret_root/binance_api_key"
printf '%s' "$api_secret" > "$secret_root/binance_api_secret"
chmod 600 "$secret_root/binance_api_key" "$secret_root/binance_api_secret"

unset api_key api_secret
echo "Secrets stored in $secret_root with directory mode 700 and file mode 600."
echo 'Do not print, commit, upload or paste these values.'
