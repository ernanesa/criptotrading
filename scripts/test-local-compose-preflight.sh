#!/usr/bin/env bash
set -Eeuo pipefail

# Offline text-only preflight. This script deliberately never invokes Docker,
# Compose, the network, or a container.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
base_compose="$repo_root/compose.yaml"
observability_compose="$repo_root/compose.observability.yaml"

fail() {
    printf 'Local Compose preflight failed: %s\n' "$1" >&2
    exit 1
}

for file in "$base_compose" "$observability_compose"; do
    [[ -r "$file" ]] || fail 'a required Compose file is missing or unreadable.'
done

require_pattern() {
    local pattern="$1" file="$2" message="$3"
    rg -q -- "$pattern" "$file" || fail "$message"
}

require_literal() {
    local literal="$1" file="$2" message="$3"
    rg -F -q -- "$literal" "$file" || fail "$message"
}

reject_pattern() {
    local pattern="$1" file="$2" message="$3"
    if rg -q -- "$pattern" "$file"; then
        fail "$message"
    fi
}

check_loopback_ports() {
    local file="$1"
    # Parse only service-level ports lists and reject any published mapping
    # that is not visibly bound to IPv4 loopback. Fail closed on unfamiliar
    # entries rather than trying to interpret all of YAML in shell.
    awk '
        /^[[:space:]]{2}[[:alnum:]_-]+:[[:space:]]*($|#)/ { in_service=1; in_ports=0 }
        in_service && /^[[:space:]]{4}ports:[[:space:]]*($|#)/ { in_ports=1; next }
        in_service && in_ports && /^[[:space:]]{4}[^[:space:]#][^:]*:/ { in_ports=0 }
        in_ports && /^[[:space:]]{6}-[[:space:]]*/ {
            line=$0
            sub(/^[[:space:]]{6}-[[:space:]]*/, "", line)
            if (line !~ /^"127\.0\.0\.1:[^"]+"[[:space:]]*($|#)/ &&
                line !~ /^\047127\.0\.0\.1:[^\047]+\047[[:space:]]*($|#)/) exit 1
            count++
        }
        END { if (count == 0) exit 1 }
    ' "$file" || fail 'a published port is absent, unrecognized, or not bound to loopback.'
}

check_loopback_ports "$base_compose"
check_loopback_ports "$observability_compose"

reject_pattern '^[[:space:]]*BINANCE_API_(KEY|SECRET)([[:space:]]*:|[[:space:]]*=)' "$base_compose" \
    'base Compose contains a Binance API key or secret assignment.'
reject_pattern '^[[:space:]]*POSTGRES_PASSWORD([[:space:]]*:|[[:space:]]*=)' "$base_compose" \
    'base Compose contains a POSTGRES_PASSWORD assignment.'
reject_pattern '^[[:space:]]*POSTGRES_CONNECTION_STRING([[:space:]]*:|[[:space:]]*=)' "$base_compose" \
    'base Compose contains a POSTGRES_CONNECTION_STRING assignment.'

require_pattern 'WORKER_EXECUTION_MODE: \$\{WORKER_EXECUTION_MODE:-PAPER\}' "$base_compose" \
    'Worker execution mode does not default to PAPER.'
require_pattern 'WORKER_NO_TRADE: \$\{WORKER_NO_TRADE:-true\}' "$base_compose" \
    'Worker NO_TRADE does not default to true.'

# Every execution-affecting switch must remain explicitly disabled by default.
for variable in \
    WORKER_AUTONOMY_ENABLED \
    WORKER_TESTNET_EXECUTION_ACKNOWLEDGED \
    WORKER_TESTNET_ORDER_EXECUTION_ENABLED \
    WORKER_PERSISTENCE_ENABLED \
    WORKER_APPLY_MIGRATIONS \
    WORKER_RISK_POLICY_REQUIRED \
    WORKER_RECONCILIATION_ENABLED \
    WORKER_ACCOUNT_STREAM_ENABLED; do
    expected="${variable}: \${"
    expected+="${variable}:-false}"
    require_literal "$expected" "$base_compose" \
        "a critical Worker switch lacks its disabled default (${variable})."
done

require_pattern '^    healthcheck:[[:space:]]*$' "$base_compose" \
    'PostgreSQL healthcheck is missing.'
require_pattern 'pg_isready -U \$\$POSTGRES_USER -d \$\$POSTGRES_DB' "$base_compose" \
    'PostgreSQL healthcheck does not probe the configured database.'

printf 'Local Compose preflight passed: static safety patterns found; no Docker, network, or containers used.\n'
