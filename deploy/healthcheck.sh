#!/bin/bash
# The HEALTHCHECK of the image, which has no curl. Only the body tells Healthy from Degraded, because both answer 200.
# HTTP/1.0 with no Host header passes each AllowedHosts value, and a Host of localhost would fail a list of real names.

port="${1:-8080}"
exec 3<>"/dev/tcp/127.0.0.1/$port" || exit 1
printf 'GET /health HTTP/1.0\r\n\r\n' >&3

body=
while IFS= read -r line <&3 || [ -n "$line" ]; do
  body="$line"
done

echo "$body"
[ "$body" = Healthy ]
