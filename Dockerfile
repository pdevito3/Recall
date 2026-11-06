# syntax=docker/dockerfile:1
# The build stages run on the platform of the builder, and the final stage has no RUN, so an image for the other
# architecture needs no emulation.
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend
WORKDIR /app
# Corepack uses the pnpm version of packageManager in package.json
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml ./
COPY frontend/patches ./patches
RUN pnpm install --frozen-lockfile
COPY frontend/ .
RUN pnpm build

FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS ffmpeg
ARG TARGETARCH
ARG FFMPEG_VERSION=7.1.3
ARG FFMPEG_SHA256=f0bf043299db9e3caacb435a712fc541fbb07df613c4b893e8b77e67baf3adbe
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gcc libc6-dev make nasm xz-utils "crossbuild-essential-$TARGETARCH" \
    && curl -fsSL -o /tmp/ffmpeg.tar.xz "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz" \
    && echo "$FFMPEG_SHA256  /tmp/ffmpeg.tar.xz" | sha256sum -c - \
    && mkdir /src && tar -xJf /tmp/ffmpeg.tar.xz -C /src --strip-components=1
WORKDIR /src
# The libm.a of the Debian cross package is a linker script that names the native path of the library.
RUN case "$TARGETARCH" in \
        amd64) arch=x86_64; prefix=x86_64-linux-gnu-; \
            sed -i 's#/usr/lib/x86_64-linux-gnu/#/usr/x86_64-linux-gnu/lib/#g' /usr/x86_64-linux-gnu/lib/libm.a ;; \
        arm64) arch=aarch64; prefix=aarch64-linux-gnu- ;; \
        *) echo "unknown target $TARGETARCH"; exit 1 ;; \
    esac \
    && ./configure --enable-cross-compile --cross-prefix="$prefix" --arch="$arch" --target-os=linux \
        --disable-everything --disable-autodetect --disable-doc --disable-network --disable-ffplay --disable-ffprobe \
        --disable-debug --enable-small --enable-static --disable-shared --extra-ldflags=-static \
        --enable-protocol=file --enable-demuxer=matroska,mov \
        --enable-decoder=opus,aac,aac_fixed --enable-parser=opus,aac \
        --enable-encoder=pcm_s16le --enable-muxer=wav --enable-filter=aresample,aformat,anull --enable-swresample \
    && make -j"$(nproc)" ffmpeg

# One SDK band, as in CI. A new band can add style errors, and a style error fails this build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props .editorconfig ./
COPY Recall.Server/*.csproj Recall.Server/
COPY Recall.SourceGenerators/*.csproj Recall.SourceGenerators/
COPY Recall.Transcriber/*.csproj Recall.Transcriber/packages.lock.json Recall.Transcriber/
RUN dotnet restore Recall.Server/Recall.Server.csproj -a $TARGETARCH
RUN dotnet restore Recall.Transcriber/Recall.Transcriber.csproj -a $TARGETARCH --locked-mode

COPY Recall.SourceGenerators/ Recall.SourceGenerators/
COPY Recall.Server/ Recall.Server/
COPY Recall.Transcriber/ Recall.Transcriber/
RUN dotnet publish Recall.Transcriber/Recall.Transcriber.csproj -c Release -a $TARGETARCH --no-restore -o /app/transcriber
WORKDIR /src/Recall.Server
RUN dotnet publish -c Release -a $TARGETARCH --no-restore -o /app/publish
RUN mkdir /empty-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /app/transcriber ./transcriber
COPY --from=ffmpeg /src/ffmpeg /usr/local/bin/ffmpeg
COPY --from=frontend /app/dist ./wwwroot
COPY deploy/healthcheck.sh .
# A new named volume takes the owner of this folder, so the user below can write to it.
COPY --from=build --chown=1654:1654 /empty-data /data

# The base image sets ASPNETCORE_HTTP_PORTS=8080. Kestrel ignores it and logs a warning, so clear it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

USER 1654:1654
EXPOSE 8080
VOLUME /data

HEALTHCHECK --interval=1h --timeout=60s --start-period=10m --start-interval=5s --retries=1 CMD ["/app/healthcheck.sh"]

ENTRYPOINT ["dotnet", "Recall.Server.dll"]
