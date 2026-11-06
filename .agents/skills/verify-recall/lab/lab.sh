#!/usr/bin/env bash
# Usage: lab.sh up | env | pocket-login-url <username> | down
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
export LAB_DIR="${LAB_DIR:-/tmp/recall-r3-lab}"
compose() { docker compose --env-file "$LAB_DIR/.env" -f "$HERE/compose.yml" "$@"; }
rand() { openssl rand -hex 32; }
up() {
  mkdir -p "$LAB_DIR/keycloak" "$LAB_DIR/authelia"
  if [ ! -f "$LAB_DIR/.env" ]; then
    local lan iface; iface="$(route -n get default | awk '/interface:/{print $2}')"; lan="$(ipconfig getifaddr "$iface" 2>/dev/null || true)"; lan="${lan:-127.0.0.1}"
    local password secret; password="$(rand)"; secret="$(rand)"
    printf 'LAB_DIR=%s\nLAB_LAN_IP=%s\nLAB_PASSWORD=%s\nLAB_CLIENT_SECRET=%s\nLAB_ENCRYPTION_KEY=%s\n' \
      "$LAB_DIR" "$lan" "$password" "$secret" "$(rand)" > "$LAB_DIR/.env"
    for realm in a b; do
      sed -e "s/__REALM__/$realm/g" -e "s/__SECRET__/$secret/g" -e "s/__PASSWORD__/$password/g" -e "s/__LAN_IP__/$lan/g" "$HERE/realm.template.json" > "$LAB_DIR/keycloak/realm-$realm.json"
    done
    openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj "/CN=auth.recall.localhost" -addext "subjectAltName=DNS:auth.recall.localhost" \
      -keyout "$LAB_DIR/authelia/tls.key" -out "$LAB_DIR/authelia/tls.crt" 2>/dev/null
    openssl genrsa -out "$LAB_DIR/authelia/oidc.pem" 2048 2>/dev/null
    local client_hash user_hash
    client_hash="$(docker run --rm authelia/authelia:4.39 authelia crypto hash generate pbkdf2 --variant sha512 --password "$secret" | sed 's/^Digest: //')"
    user_hash="$(docker run --rm authelia/authelia:4.39 authelia crypto hash generate argon2 --password "$password" | sed 's/^Digest: //')"
    sed -e "s/__R1__/$(rand)/" -e "s/__R2__/$(rand)/" -e "s/__R3__/$(rand)/" -e "s/__R4__/$(rand)/" -e "s/__LAN_IP__/$lan/g" -e "s|__CLIENT_HASH__|$client_hash|" \
      "$HERE/authelia.template.yml" > "$LAB_DIR/authelia/configuration.yml"
    printf 'users:\n  alice:\n    displayname: "Alice Authelia"\n    password: "%s"\n    email: alice-authelia@example.com\n    groups: []\n' "$user_hash" > "$LAB_DIR/authelia/users.yml"
    cat > "$LAB_DIR/Caddyfile" <<CADDY
{
  auto_https disable_redirects
}
https://recall.localhost:24443 {
  tls internal
  reverse_proxy host.docker.internal:24000
}
CADDY
  fi
  compose up -d
  until curl -fsS http://localhost:28180/realms/a/.well-known/openid-configuration >/dev/null 2>&1; do sleep 2; done
  until curl -fsS http://localhost:28181/realms/b/.well-known/openid-configuration >/dev/null 2>&1; do sleep 2; done
  until curl -fsSk --resolve auth.recall.localhost:29191:127.0.0.1 https://auth.recall.localhost:29191/.well-known/openid-configuration >/dev/null 2>&1; do sleep 2; done
  until curl -fsS http://localhost:29411/.well-known/openid-configuration >/dev/null 2>&1; do sleep 2; done
  pocket_setup
  env_out
}
pocket_api() { curl -fsS -H "X-API-Key: $(cat "$LAB_DIR/pocket.key")" -H 'Content-Type: application/json' "$@"; }
pocket_setup() {
  . "$LAB_DIR/.env"
  [ -f "$LAB_DIR/pocket.env" ] && return 0
  local base=http://localhost:29411 cookie client_json client_id client_secret
  cookie="$(curl -fsS -i -X POST -H 'Content-Type: application/json' "$base/api/signup/setup" -d '{"username":"labadmin","email":"labadmin@example.com","firstName":"Lab","lastName":"Admin"}' | sed -n 's/^[Ss]et-[Cc]ookie: \(access_token=[^;]*\).*/\1/p')"
  curl -fsS -H "Cookie: $cookie" -H 'Content-Type: application/json' -X POST "$base/api/api-keys" -d "{\"name\":\"lab\",\"expiresAt\":\"$(date -u -v+7d +%Y-%m-%dT%H:%M:%SZ)\"}" | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])' > "$LAB_DIR/pocket.key"
  client_json="$(pocket_api -X POST "$base/api/oidc/clients" -d "{\"name\":\"Recall\",\"callbackURLs\":[\"http://localhost:24000/auth/oidc/callback\",\"http://$LAB_LAN_IP:24000/auth/oidc/callback\",\"https://recall.localhost:24443/auth/oidc/callback\"],\"logoutCallbackURLs\":[],\"isPublic\":false,\"pkceEnabled\":true}")"
  client_id="$(printf '%s' "$client_json" | python3 -c 'import json,sys;print(json.load(sys.stdin)["id"])')"
  client_secret="$(pocket_api -X POST "$base/api/oidc/clients/$client_id/secret" | python3 -c 'import json,sys;print(json.load(sys.stdin)["secret"])')"
  pocket_api -X POST "$base/api/users" -d '{"username":"alice","email":"alice-pocket@example.com","firstName":"Alice","lastName":"Pocket","displayName":"Alice Pocket","isAdmin":false}' >/dev/null
  printf 'POCKET_CLIENT_ID=%s\nPOCKET_CLIENT_SECRET=%s\n' "$client_id" "$client_secret" > "$LAB_DIR/pocket.env"
}
pocket_login_url() {
  local user="$1" id token
  id="$(pocket_api "http://localhost:29411/api/users?search=$user" | python3 -c 'import json,sys;print(json.load(sys.stdin)["data"][0]["id"])')"
  token="$(pocket_api -X POST "http://localhost:29411/api/users/$id/one-time-access-token" -d "{\"ttl\":\"10m\",\"userId\":\"$id\"}" | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')"
  echo "http://localhost:29411/lc/$token"
}
env_out() {
  . "$LAB_DIR/.env"; . "$LAB_DIR/pocket.env"
  cat <<OUT
# Paste into the shell that runs the Server. Index 0 Keycloak a, 1 Keycloak b, 2 Authelia, 3 Pocket ID.
export Auth__OpenSignup=true
export Auth__Providers__0__Id=keycloak-a Auth__Providers__0__DisplayName='Keycloak A' Auth__Providers__0__Issuer=http://localhost:28180/realms/a Auth__Providers__0__ClientId=recall Auth__Providers__0__ClientSecret=$LAB_CLIENT_SECRET Auth__Providers__0__AllowHttp=true
export Auth__Providers__1__Id=keycloak-b Auth__Providers__1__DisplayName='Keycloak B' Auth__Providers__1__Issuer=http://localhost:28181/realms/b Auth__Providers__1__ClientId=recall Auth__Providers__1__ClientSecret=$LAB_CLIENT_SECRET Auth__Providers__1__AllowHttp=true
export Auth__Providers__2__Id=authelia Auth__Providers__2__DisplayName=Authelia Auth__Providers__2__Issuer=https://auth.recall.localhost:29191 Auth__Providers__2__ClientId=recall Auth__Providers__2__ClientSecret=$LAB_CLIENT_SECRET Auth__Providers__2__CaCertificatePath=$LAB_DIR/authelia/tls.crt
export Auth__Providers__3__Id=pocket-id Auth__Providers__3__DisplayName='Pocket ID' Auth__Providers__3__Issuer=http://localhost:29411 Auth__Providers__3__ClientId=$POCKET_CLIENT_ID Auth__Providers__3__ClientSecret=$POCKET_CLIENT_SECRET Auth__Providers__3__AllowHttp=true
# Test user password for alice and bob at Keycloak, and alice at Authelia.
export LAB_PASSWORD=$LAB_PASSWORD LAB_LAN_IP=$LAB_LAN_IP
OUT
}
case "${1:-}" in
  up) up ;;
  env) env_out ;;
  pocket-login-url) pocket_login_url "$2" ;;
  down) compose down -v; rm -rf "$LAB_DIR" ;;
  *) echo "usage: lab.sh up|env|pocket-login-url <username>|down" >&2; exit 2 ;;
esac
