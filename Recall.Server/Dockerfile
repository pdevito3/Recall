# Build frontend
FROM node:22-alpine AS frontend
WORKDIR /app
# Corepack uses the pnpm version of packageManager in package.json
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml ./
COPY frontend/patches ./patches
RUN pnpm install --frozen-lockfile
COPY frontend/ .
RUN pnpm build

# Build .NET application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props Directory.Build.props .editorconfig ./
COPY Recall.Server/*.csproj Recall.Server/
COPY Recall.SourceGenerators/*.csproj Recall.SourceGenerators/
RUN dotnet restore Recall.Server/Recall.Server.csproj

COPY Recall.SourceGenerators/ Recall.SourceGenerators/
COPY Recall.Server/ Recall.Server/
WORKDIR /src/Recall.Server
RUN dotnet publish -c Release -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
# The Server serves the SPA from its web root
COPY --from=frontend /app/dist ./wwwroot

# Kestrel:Endpoints in appsettings.json binds 8080 (HTTP/1.1) and 8081 (HTTP/2 only, h2c, for gRPC).
# The base image sets ASPNETCORE_HTTP_PORTS=8080. Kestrel ignores it and logs a warning, so clear it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

EXPOSE 8080 8081

# The aspnet image has no curl, so the image has no HEALTHCHECK. The host probes /health itself.

ENTRYPOINT ["dotnet", "Recall.Server.dll"]
