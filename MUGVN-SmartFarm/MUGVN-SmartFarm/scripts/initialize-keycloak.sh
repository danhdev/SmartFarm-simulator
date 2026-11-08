#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
source .env
set +a
# Passwords enter the container over stdin, never as docker command arguments.
printf '%s\n%s\n%s\n%s\n' "$KEYCLOAK_ADMIN_USERNAME" "$KEYCLOAK_ADMIN_PASSWORD" "$FARM_ADMIN_USERNAME" "$FARM_ADMIN_PASSWORD" |
docker compose exec -T keycloak bash -c '
set -euo pipefail
read -r admin_user; read -r admin_password; read -r farm_user; read -r farm_password
cli=/opt/keycloak/bin/kcadm.sh
$cli config credentials --server http://localhost:8080 --realm master --user "$admin_user" --password "$admin_password" >/dev/null
users=$($cli get users -r smartfarm -q "username=$farm_user" -q exact=true --fields id --format csv --noquotes)
id=$(printf "%s\n" "$users" | tr -d "\r" | sed "/^id$/d; /^$/d" | head -n 1)
if [ -z "$id" ]; then
  id=$($cli create users -r smartfarm -s "username=$farm_user" -s "email=$farm_user" -s enabled=true -s emailVerified=true -s firstName=Farm -s lastName=Administrator -i)
  $cli set-password -r smartfarm --userid "$id" --new-password "$farm_password"
fi
$cli add-roles -r smartfarm --uid "$id" --rolename farm-admin
client=$($cli get clients -r smartfarm -q clientId=smartfarm-demo --fields id --format csv --noquotes | tr -d "\r" | sed "/^id$/d; /^$/d" | head -n 1)
if [ -z "$client" ]; then
  printf "smartfarm-demo client was not found.\n" >&2
  exit 1
fi
$cli update "clients/$client" -r smartfarm \
  -s "redirectUris=[\"https://localhost:7017/demo/\",\"http://localhost:5198/demo/\",\"http://localhost:5173/\"]" \
  -s "webOrigins=[\"https://localhost:7017\",\"http://localhost:5198\",\"http://localhost:5173\"]" \
  -s "attributes.\"post.logout.redirect.uris\"=https://localhost:7017/demo/##http://localhost:5198/demo/##http://localhost:5173/"
rm -f /opt/keycloak/.keycloak/kcadm.config 2>/dev/null || true
printf "Farm administrator and frontend redirects initialized; existing passwords were preserved.\n"
'
