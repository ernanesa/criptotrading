#!/usr/bin/env bash
set -Eeuo pipefail

# Deterministic local PAPER/NO_TRADE chaos evidence. This test is intentionally
# isolated from Binance, PostgreSQL and all credential sources.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_command="dotnet"
if [[ -x "$repo_root/.tools/dotnet/dotnet" ]]; then
    dotnet_command="$repo_root/.tools/dotnet/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" && -x "$DOTNET_ROOT/dotnet" ]]; then
    dotnet_command="$DOTNET_ROOT/dotnet"
fi

env -u BINANCE_API_KEY -u BINANCE_API_SECRET "$dotnet_command" test \
    "$repo_root/tests/CryptoTrading.Worker.Tests/CryptoTrading.Worker.Tests.csproj" \
    --no-restore \
    --configuration Release \
    --filter 'Category=PaperChaos' \
    --logger 'console;verbosity=minimal'

echo "Paper soak/chaos evidence passed: deterministic restarts, transient failures, idempotency, and no duplicate simulated orders."
