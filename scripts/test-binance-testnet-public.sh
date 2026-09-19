#!/usr/bin/env bash
set -euo pipefail

base_url="${BINANCE_REST_BASE_URL:-https://testnet.binance.vision/api}"
case "$base_url" in
  https://testnet.binance.vision/api) ;;
  *) echo 'Refusing to test: BINANCE_REST_BASE_URL is not the Spot Testnet URL.' >&2; exit 1 ;;
esac

curl --fail-with-body --silent --show-error --max-time 10 "$base_url/v3/ping" >/dev/null
curl --fail-with-body --silent --show-error --max-time 10 "$base_url/v3/time" >/dev/null
curl --fail-with-body --silent --show-error --max-time 10 \
  "$base_url/v3/exchangeInfo?symbol=BTCUSDT" >/dev/null
echo 'Binance Spot Testnet public boundary: OK (ping, time, exchangeInfo BTCUSDT).'
