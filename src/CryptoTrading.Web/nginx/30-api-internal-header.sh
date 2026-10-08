#!/bin/sh
set -eu

secret_path=/run/secrets/api_internal_access_token
include_dir=/tmp/nginx
include_path="$include_dir/api-internal-access-token.conf"

if [ ! -r "$secret_path" ]; then
    echo "API internal access token secret is unavailable." >&2
    exit 1
fi

token="$(tr -d '\r\n' < "$secret_path")"
if [ -z "$token" ]; then
    echo "API internal access token secret is empty." >&2
    exit 1
fi

case "$token" in
    *[!A-Za-z0-9._~-]*)
        echo "API internal access token must use URL-safe token characters." >&2
        exit 1
        ;;
esac

mkdir -p "$include_dir"
umask 077
printf 'proxy_set_header X-Internal-Access-Token "%s";\n' "$token" > "$include_path"
