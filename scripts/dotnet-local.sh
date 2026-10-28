#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_root="${DOTNET_LOCAL_ROOT:-$repo_root/.tools/dotnet}"
dotnet_executable="$dotnet_root/dotnet"

if [[ ! -x "$dotnet_executable" ]]; then
    echo "Pinned local .NET SDK is missing at $dotnet_executable; install the exact SDK from global.json before using this launcher." >&2
    exit 127
fi

export DOTNET_ROOT="$dotnet_root"
export PATH="$dotnet_root:$PATH"
exec "$dotnet_executable" "$@"
