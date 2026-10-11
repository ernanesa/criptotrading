#!/usr/bin/env bash
set -Eeuo pipefail

# Starts disposable containers from a temporary working directory. It does not
# read repository .env files, reuse an application volume, or invoke exchanges.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run_id="$$-$(date +%s)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/cryptotrading-container-smoke.XXXXXX")"
project_name="cryptotrading-smoke-$run_id"
network_name="cryptotrading-observability-smoke-$run_id"
postgres_volume="cryptotrading-smoke-postgres-$run_id"
web_port=$((58000 + ($$ % 500)))
api_port=$((58500 + ($$ % 500)))
otel_grpc_port=$((59000 + ($$ % 500)))
otel_http_port=$((59500 + ($$ % 400)))
otel_health_port=$((59900 + ($$ % 90)))
prometheus_port=$((60000 + ($$ % 500)))
grafana_port=$((60500 + ($$ % 500)))
tempo_port=$((61000 + ($$ % 500)))
token_file="$work_dir/api-internal-access-token"

umask 077
openssl rand -hex 32 > "$token_file"
password="$(openssl rand -hex 32)"
pg_secrets_dir="$work_dir/postgres-secrets"
mkdir -m 0700 "$pg_secrets_dir"
printf '%s' "$password" > "$pg_secrets_dir/postgres_password"
chmod 0600 "$pg_secrets_dir/postgres_password"

export API_INTERNAL_ACCESS_TOKEN_SOURCE="$token_file"
export POSTGRES_SECRETS_DIRECTORY="$pg_secrets_dir"
export LOCAL_UID="$(id -u)"
export LOCAL_GID="$(id -g)"
export API_PORT="$api_port"
export WEB_PORT="$web_port"
export OTEL_GRPC_PORT="$otel_grpc_port"
export OTEL_HTTP_PORT="$otel_http_port"
export OTEL_HEALTH_PORT="$otel_health_port"
export PROMETHEUS_PORT="$prometheus_port"
export GRAFANA_PORT="$grafana_port"
export TEMPO_PORT="$tempo_port"
export LOKI_PORT="$((62000 + ($$ % 500)))"
export OBSERVABILITY_NETWORK_NAME="$network_name"
export POSTGRES_DB=cryptotrading
export POSTGRES_USER=cryptotrading
export POSTGRES_PORT="$((61500 + ($$ % 500)))"
export POSTGRES_VOLUME_NAME="$postgres_volume"
export APP_ENVIRONMENT=Production
export WORKER_EXECUTION_MODE=PAPER
export WORKER_NO_TRADE=true
export WORKER_AUTONOMY_ENABLED=false
export WORKER_PERSISTENCE_ENABLED=true WORKER_APPLY_MIGRATIONS=true
export WORKER_RECONCILIATION_ENABLED=false WORKER_ACCOUNT_STREAM_ENABLED=false WORKER_RISK_POLICY_REQUIRED=false
export BINANCE_API_KEY=
export BINANCE_API_SECRET=

app_compose=(docker compose --env-file /dev/null --project-directory "$repo_root" --project-name "$project_name" --file "$repo_root/compose.yaml")
observability_compose=(docker compose --env-file /dev/null --project-directory "$repo_root" --project-name "${project_name}-observability" --file "$repo_root/compose.observability.yaml")

cleanup() {
    "${app_compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    "${observability_compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
    docker network rm "$network_name" >/dev/null 2>&1 || true
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

docker network create "$network_name" >/dev/null
"${app_compose[@]}" up --build -d postgres cryptotrading-worker cryptotrading-api cryptotrading-web >/dev/null
export CRYPTO_API_CONTAINER_ID="$(docker inspect --format '{{.Id}}' "$("${app_compose[@]}" ps -q cryptotrading-api)")"
export CRYPTO_WEB_CONTAINER_ID="$(docker inspect --format '{{.Id}}' "$("${app_compose[@]}" ps -q cryptotrading-web)")"
export CRYPTO_WORKER_CONTAINER_ID="$(docker inspect --format '{{.Id}}' "$("${app_compose[@]}" ps -q cryptotrading-worker)")"
"${observability_compose[@]}" up -d loki otel-collector >/dev/null
echo "Smoke services started. WebPort=$web_port Project=$project_name"

for attempt in {1..45}; do
    if curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:$web_port/health/live" >/dev/null 2>&1 &&
       [[ "$(curl --silent --max-time 2 --output "$work_dir/status.json" --write-out '%{http_code}' "http://127.0.0.1:$web_port/operational/status")" == 503 ]] &&
       rg -q 'OPERATIONAL_STATE_UNAVAILABLE|unavailable' "$work_dir/status.json"; then
        break
    fi
    sleep 1
    if (( attempt == 45 )); then
        "${app_compose[@]}" ps --format '{{.Service}} {{.State}}' >&2
        echo "Liveness HTTP: $(curl --silent --max-time 2 --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$web_port/health/live")" >&2
        echo "Status HTTP: $(curl --silent --max-time 2 --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$web_port/operational/status")" >&2
        echo 'Container smoke failed before Web could reach the API through the internal proxy.' >&2
        exit 1
    fi
done

for attempt in {1..45}; do
    if curl --silent --fail --max-time 3 --get "http://127.0.0.1:$LOKI_PORT/loki/api/v1/query_range" \
        --data-urlencode 'query={service_name="cryptotrading-docker"} |= "/operational/status"' \
        --data-urlencode 'limit=5' --output "$work_dir/loki-result.json" &&
       python3 -c 'import json,sys; r=json.load(open(sys.argv[1])); sys.exit(0 if r.get("data",{}).get("result") else 1)' "$work_dir/loki-result.json"; then
        echo 'Container smoke passed: authenticated relative API proxy and project-scoped Docker logs queried in Loki.'
        exit 0
    fi
    sleep 1
done
echo 'Container smoke failed: project log delivery to Loki was not proven.' >&2
exit 1
