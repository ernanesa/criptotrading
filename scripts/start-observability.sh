#!/usr/bin/env bash
set -Eeuo pipefail
# Select only this project's containers. No environment, credential or log
# payload is inspected. Rerun after application containers are recreated.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_name="${APP_COMPOSE_PROJECT_NAME:-cryptotrading}"
container_id() {
    local service="$1"
    local ids
    ids="$(docker ps --no-trunc --quiet --filter "label=com.docker.compose.project=$project_name" --filter "label=com.docker.compose.service=$service")"
    if [[ ! "$ids" =~ ^[a-f0-9]{64}$ ]]; then
        echo "Expected one running $service container for project $project_name." >&2
        return 1
    fi
    printf '%s' "$ids"
}
export CRYPTO_API_CONTAINER_ID="$(container_id cryptotrading-api)"
export CRYPTO_WORKER_CONTAINER_ID="$(container_id cryptotrading-worker)"
export CRYPTO_WEB_CONTAINER_ID="$(container_id cryptotrading-web)"
docker compose --project-directory "$repo_root" --file "$repo_root/compose.observability.yaml" up -d
