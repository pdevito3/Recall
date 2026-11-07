#!/bin/bash
# The HEALTHCHECK of the image, which has no curl. Only the body tells Healthy from Degraded, because both answer 200.
# HTTP/1.0 with no Host header passes each AllowedHosts value, and a Host of localhost would fail a list of real names.

port="${1:-8080}"
seconds_between_attempts="${2:-10}"
attempts_for_a_disk_to_wake=4
newline=$'\n'

read_health() {
  body=
  exec 3<>"/dev/tcp/127.0.0.1/$port" || return
  printf 'GET /health HTTP/1.0\r\n\r\n' >&3
  in_body=
  while IFS= read -r line <&3 || [ -n "$line" ]; do
    if [ -n "$in_body" ]; then
      body="${body:+$body$newline}$line"
    elif [ "$line" = $'\r' ]; then
      in_body=1
    fi
  done
  exec 3<&-
}

for ((attempt = 1; attempt <= attempts_for_a_disk_to_wake; attempt++)); do
  [ "$attempt" -gt 1 ] && sleep "$seconds_between_attempts"
  read_health 2>/dev/null
  [ "$body" = Healthy ] && break
done

echo "${body:-No answer from /health}"
[ "$body" = Healthy ]
