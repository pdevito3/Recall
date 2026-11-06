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

# One SDK band, as in CI. A new band can add style errors, and a style error fails this build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props .editorconfig ./
COPY Recall.Server/*.csproj Recall.Server/
COPY Recall.SourceGenerators/*.csproj Recall.SourceGenerators/
RUN dotnet restore Recall.Server/Recall.Server.csproj -a $TARGETARCH

COPY Recall.SourceGenerators/ Recall.SourceGenerators/
COPY Recall.Server/ Recall.Server/
WORKDIR /src/Recall.Server
RUN dotnet publish -c Release -a $TARGETARCH --no-restore -o /app/publish
RUN mkdir /empty-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
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

HEALTHCHECK --interval=1h --timeout=60s --start-period=2m --start-interval=5s --retries=1 CMD ["/app/healthcheck.sh"]

ENTRYPOINT ["dotnet", "Recall.Server.dll"]
