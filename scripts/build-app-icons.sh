#!/usr/bin/env bash
# Builds every web icon of Recall from the 1024x1024 icon of the iOS app.
# Run it again when the iOS icon changes, then bump the ?v= query in frontend/index.html,
# frontend/public/manifest.webmanifest, and frontend/src/components/app-sidebar.tsx.
#
#   scripts/build-app-icons.sh path/to/AppIcon.png
#
# The source is JournalRecall/Assets.xcassets/AppIcon.appiconset/AppIcon.png in pdevito3/Recall-ios.
# It is opaque, and its corners outside the rounded square hold the background of the render.
# The script cuts those corners to transparent for each icon that a page or a browser shows as is.
# apple-touch-icon.png stays opaque, because iOS puts its own mask on it.
# Needs ImageMagick 7 (magick).
set -euo pipefail

source=${1:?Give the path of the 1024x1024 AppIcon.png}
root=$(cd "$(dirname "$0")/.." && pwd)
public="$root/frontend/public"
auth="$root/Recall.Server/Resources/Entrypoints/Auth"

# 230 of 1024 is the corner radius of an iOS icon, and it cuts all of the background of the render.
radius=230

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

size=$(magick identify -format '%wx%h' "$source")
[[ $size == 1024x1024 ]] || { echo "The source is $size, not 1024x1024." >&2; exit 1; }

magick "$source" -alpha set \
  \( -size 1024x1024 xc:none -fill white -draw "roundrectangle 0,0 1023,1023 $radius,$radius" \) \
  -compose DstIn -composite "$work/masked.png"

# 256 colors keep the paper texture and the soft edge of the corners, at about a third of the bytes.
# -strip drops the dates and the color profile chunks, so a second run gives the same bytes.
png() { # input size output
  magick "$1" -filter Lanczos -resize "$2x$2" -colors 256 -strip \
    -define png:compression-level=9 -define png:compression-filter=5 -define png:exclude-chunks=date,time "$3"
}

png "$work/masked.png" 32 "$public/favicon-32.png"
png "$work/masked.png" 64 "$public/favicon.png"
png "$work/masked.png" 192 "$public/icon-192.png"
png "$work/masked.png" 512 "$public/icon-512.png"
png "$source" 180 "$public/apple-touch-icon.png"
# The login pages of the Server show this one at 48 CSS pixels, so it is sharp on a 2x screen.
png "$work/masked.png" 96 "$auth/recall-icon.png"

magick "$work/masked.png" -filter Lanczos -define icon:auto-resize=48,32,16 -strip "$public/favicon.ico"

ls -l "$public"/favicon* "$public"/icon-*.png "$public/apple-touch-icon.png" "$auth/recall-icon.png"
