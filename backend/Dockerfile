FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Central build props for CPM (must be present before restore).
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY global.json ./

# Copy the project file first for restore caching.
COPY backend/FloodRisk.Api.csproj backend/

RUN dotnet restore backend/FloodRisk.Api.csproj

# Copy remaining source.
COPY . .

WORKDIR /src/backend
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl is only here for the HEALTHCHECK probe below.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# Run as the image's built-in non-root user. It cannot bind ports below 1024, so the
# default listener is 8080 (Render overrides it with ASPNETCORE_URLS and its own probe).
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --retries=5 --start-period=20s CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "FloodRisk.Api.dll"]
