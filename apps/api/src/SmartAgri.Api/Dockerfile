# Build context is the repository root (see deploy/docker-compose.yml) so global.json and the
# frontends' sources are available; the root .dockerignore limits it to what is copied below.
# Both frontends are built here and served by the api itself:
# - the admin (apps/admin, pre-launch #306) in API mode (production-api), so one address serves the
#   admin, the API, LINE and the website embed; the admin signs in through /connect/* on its own origin;
# - the chat window customers embed (apps/widget, M5a #201): its frame-ancestors depend on each
#   assistant's allowed domains, which a static host cannot do.
# Node 24 as in .nvmrc; --ignore-scripts keeps devDependencies' postinstall downloads (Cypress) out
# of the image build.
FROM node:24-slim AS frontend
WORKDIR /repo
ENV NX_DAEMON=false NX_NO_CLOUD=true
COPY package.json package-lock.json .npmrc ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY nx.json tsconfig.base.json .postcssrc.json ./
COPY libs libs
COPY apps/widget apps/widget
COPY apps/admin apps/admin
# The admin first: Tailwind scans the workspace for class names, and there is no .gitignore here to
# keep it out of a dist/ that already holds the widget's build.
RUN npx nx build admin --configuration=production-api --skip-nx-cache
RUN npx nx build widget --configuration=production --skip-nx-cache

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
# Widget:RootPath and Widget:EmbedScriptPath default to these (relative to /app, the content root).
# Both are served by GET /use/{id}, /widget/* and /embed.js, never as ordinary static files.
COPY --from=frontend /repo/dist/smart-agri-widget/browser ./wwwroot/widget
# The admin SPA: served for every GET that is not one of the api's own paths (AdminSpaHosting).
# Set Admin__RootPath to empty to serve no admin from this container.
COPY --from=frontend /repo/dist/smart-agri-admin/browser ./wwwroot/admin
ENV Admin__RootPath=wwwroot/admin
COPY apps/embed-loader/src/embed.js ./wwwroot/embed.js
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
