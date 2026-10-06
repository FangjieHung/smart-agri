# Build context is the repository root (see deploy/docker-compose.yml) so global.json
# is available; the root .dockerignore limits it to what is copied below.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./global.json
COPY apps/api/Directory.Build.props apps/api/Directory.Packages.props apps/api/
COPY apps/api/src apps/api/src

WORKDIR /src/apps/api
RUN dotnet restore src/SmartAgri.Api/SmartAgri.Api.csproj --locked-mode
RUN dotnet publish src/SmartAgri.Api/SmartAgri.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime image: aspnet base images ship a non-root `app` user (uid set via $APP_UID)
# since .NET 8; on-prem-packaging ADR requires the customer-deploy api container to run
# as non-root.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY apps/api/src/SmartAgri.Api/entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

# Data Protection key ring (deploy/docker-compose.yml mounts the named volume
# smartagri-dataprotection-keys here). Docker copies the owner of an image directory into a
# new empty named volume, so creating it here, owned by the non-root user, is what lets the
# api write its keys; a directory that does not exist in the image would be created root-owned.
RUN mkdir /app/keys && chown $APP_UID:$APP_UID /app/keys

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["./entrypoint.sh"]
