#!/bin/sh
set -eu

out=/etc/nginx/conf.d/api-auth.inc
token="${LIBATION_API_TOKEN:-}"
if [ -z "$token" ] && [ -n "${LIBATION_API_TOKEN_FILE:-}" ]; then
    token="$(cat "$LIBATION_API_TOKEN_FILE")"
fi
token="$(printf '%s' "$token" | tr -d '\r\n')"

case "$token" in
    *[!A-Za-z0-9._~+/=-]*)
        echo "30-api-auth: token has characters outside [A-Za-z0-9._~+/=-]" >&2
        exit 1
        ;;
    "") echo 'proxy_set_header Authorization "";' > "$out" ;;
    *) echo "proxy_set_header Authorization \"Bearer $token\";" > "$out" ;;
esac
