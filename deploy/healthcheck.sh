#!/bin/bash
# The HEALTHCHECK of the image, which has no curl. Only the body tells Healthy from Degraded, because both answer 200.
# HTTP/1.0 with no Host header passes each AllowedHosts value, and a Host of localhost would fail a list of real names.
# /health waits 5 s for its write to the file store, and a disk that sleeps can take longer to wake, so a probe asks again.

port="${1:-8080}"
seconds_between_attempts="${2:-10}"

read_health() {
  body=
  exec 3<>"/dev/tcp/127.0.0.1/$port" || return
  printf 'GET /health HTTP/1.0\r\n\r\n' >&3
  while IFS= read -r line <&3 || [ -n "$line" ]; do
    body="$line"
  done
  exec 3<&-
}

for attempt in 1 2 3 4; do
  [ "$attempt" -gt 1 ] && sleep "$seconds_between_attempts"
  read_health 2>/dev/null
  [ "$body" = Healthy ] && break
done

echo "${body:-No answer from /health}"
[ "$body" = Healthy ]
