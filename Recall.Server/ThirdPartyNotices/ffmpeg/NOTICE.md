# FFmpeg

The image carries a static build of FFmpeg 7.1.3 at `/usr/local/bin/ffmpeg`. Recall runs it as a separate program to decode audio.

FFmpeg is licensed under the GNU Lesser General Public License, version 2.1 or later. The text of the license is in `COPYING.LGPLv2.1`. This build enables no GPL or nonfree part.

Source: https://ffmpeg.org/releases/ffmpeg-7.1.3.tar.xz

SHA-256 of the source: `f0bf043299db9e3caacb435a712fc541fbb07df613c4b893e8b77e67baf3adbe`

The `Dockerfile` of Recall holds the build commands and the configure flags.
