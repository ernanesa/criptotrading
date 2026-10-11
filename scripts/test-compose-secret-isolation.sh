#!/usr/bin/env bash
set -Eeuo pipefail

# Offline structural contract: no Docker daemon, image build, network, or
# exchange access. It proves the base Compose file cannot inject Binance
# credentials and that the separate Testnet override remains file-scoped and
# PAPER/NO_TRADE.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
base_compose="$repo_root/compose.yaml"
testnet_compose="$repo_root/compose.testnet.yaml"

require_pattern() {
    local pattern="$1"
    local file="$2"
    local message="$3"
    rg -q -- "$pattern" "$file" || { echo "$message" >&2; exit 1; }
}

reject_pattern() {
    local pattern="$1"
    local file="$2"
    local message="$3"
    if rg -q -- "$pattern" "$file"; then
        echo "$message" >&2
        exit 1
    fi
}

reject_pattern '^[[:space:]]*BINANCE_API_KEY[[:space:]]*:' "$base_compose" \
    'Base Compose must not inject BINANCE_API_KEY.'
reject_pattern '^[[:space:]]*BINANCE_API_SECRET[[:space:]]*:' "$base_compose" \
    'Base Compose must not inject BINANCE_API_SECRET.'
reject_pattern '^[[:space:]]*BINANCE_API_(KEY|SECRET)_FILE[[:space:]]*:' "$base_compose" \
    'Base Compose must not inject Binance credential file paths.'
require_pattern 'WORKER_EXECUTION_MODE: \$\{WORKER_EXECUTION_MODE:-PAPER\}' "$base_compose" \
    'Base Compose must default to PAPER execution.'
require_pattern 'WORKER_NO_TRADE: \$\{WORKER_NO_TRADE:-true\}' "$base_compose" \
    'Base Compose must default to NO_TRADE.'
require_pattern 'BINANCE_WITHDRAWALS_ENABLED: "false"' "$base_compose" \
    'Base Compose must keep withdrawals disabled.'

require_pattern 'BINANCE_API_KEY_FILE: /run/cryptotrading-testnet-secrets/binance_api_key' "$testnet_compose" \
    'Testnet override must use the scoped API-key file path.'
require_pattern 'BINANCE_API_SECRET_FILE: /run/cryptotrading-testnet-secrets/binance_api_secret' "$testnet_compose" \
    'Testnet override must use the scoped API-secret file path.'
require_pattern 'BINANCE_TESTNET_SECRETS_DIRECTORY:\?' "$testnet_compose" \
    'Testnet override must require an explicit secret directory.'
require_pattern 'target: /run/cryptotrading-testnet-secrets' "$testnet_compose" \
    'Testnet override must bind the scoped secret directory.'
require_pattern 'read_only: true' "$testnet_compose" \
    'Testnet secret directory must be read-only in the container.'
reject_pattern '^[[:space:]]*BINANCE_API_KEY[[:space:]]*:' "$testnet_compose" \
    'Testnet override must not inject BINANCE_API_KEY.'
reject_pattern '^[[:space:]]*BINANCE_API_SECRET[[:space:]]*:' "$testnet_compose" \
    'Testnet override must not inject BINANCE_API_SECRET.'
require_pattern 'BINANCE_WITHDRAWALS_ENABLED: "false"' "$testnet_compose" \
    'Testnet override must keep withdrawals disabled.'
require_pattern 'WORKER_EXECUTION_MODE: PAPER' "$testnet_compose" \
    'Testnet override must remain PAPER.'
require_pattern 'WORKER_NO_TRADE: "true"' "$testnet_compose" \
    'Testnet override must remain NO_TRADE.'
require_pattern 'WORKER_TESTNET_EXECUTION_ACKNOWLEDGED: "false"' "$testnet_compose" \
    'Testnet acknowledgement must remain disabled.'
require_pattern 'WORKER_TESTNET_ORDER_EXECUTION_ENABLED: "false"' "$testnet_compose" \
    'Testnet order execution must remain disabled.'

echo 'Compose secret-isolation contract passed: base is credential-free; Testnet is file-scoped and non-mutating.'
