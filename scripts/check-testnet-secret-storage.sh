#!/usr/bin/env bash
set -Eeuo pipefail

fail() {
    echo "Secret storage check failed: $1" >&2
    exit 2
}

[[ "${TRADING_ENVIRONMENT:-TESTNET}" == "TESTNET" ]] || fail 'TRADING_ENVIRONMENT must be TESTNET.'
[[ "${BINANCE_REST_BASE_URL:-https://testnet.binance.vision/api}" == "https://testnet.binance.vision/api" ]] || fail 'BINANCE_REST_BASE_URL must be the exact Spot Testnet endpoint.'
[[ "${BINANCE_WITHDRAWALS_ENABLED:-false}" == "false" ]] || fail 'BINANCE_WITHDRAWALS_ENABLED must be false.'

validate_file_secret() {
    local variable_name="$1"
    local path="${!variable_name:-}"
    [[ -n "$path" ]] || fail "$variable_name is empty."
    [[ "$path" == /* ]] || fail "$variable_name must be an absolute path."
    [[ -f "$path" && ! -L "$path" ]] || fail "$variable_name must reference a regular non-symlink file."

    local file_mode
    file_mode="$(stat -c '%a' -- "$path")"
    [[ "$file_mode" == "600" ]] || fail "$variable_name must reference a file with mode 600."

    local directory_mode directory
    directory="$(dirname -- "$path")"
    directory_mode=$((8#$(stat -c '%a' -- "$directory")))
    (( (directory_mode & 077) == 0 )) || fail "The directory containing $variable_name must be private."

    local byte_count
    byte_count="$(wc -c < "$path")"
    (( byte_count > 0 )) || fail "$variable_name file is empty."
    if LC_ALL=C grep -q '[[:space:]]' -- "$path"; then
        fail "$variable_name file contains whitespace."
    fi
}

if [[ -n "${BINANCE_API_KEY:-}" || -n "${BINANCE_API_SECRET:-}" ]]; then
    [[ -n "${BINANCE_API_KEY:-}" && -n "${BINANCE_API_SECRET:-}" ]] || fail 'Both direct credential values are required.'
    echo 'Secret storage check passed: direct environment credentials supplied; values were not printed.'
    exit 0
fi

[[ -n "${BINANCE_API_KEY_FILE:-}" && -n "${BINANCE_API_SECRET_FILE:-}" ]] || fail 'Provide both direct credentials or both protected credential file paths.'
validate_file_secret BINANCE_API_KEY_FILE
validate_file_secret BINANCE_API_SECRET_FILE
echo 'Secret storage check passed: protected credential files are private, non-empty and redacted.'
