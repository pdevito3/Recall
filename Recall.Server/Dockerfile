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

# Kestrel:Endpoints in appsettings.json binds 8080 (HTTP/1.1) and 8081 (HTTP/2 only, h2c, for gRPC).
# The base image sets ASPNETCORE_HTTP_PORTS=8080. Kestrel ignores it and logs a warning, so clear it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=

EXPOSE 8080 8081

# The aspnet image has no curl, so the image has no HEALTHCHECK. The host probes /health itself.

ENTRYPOINT ["dotnet", "Recall.Server.dll"]
