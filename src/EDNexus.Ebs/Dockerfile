# Build and run the EDNexus Twitch Extension Backend Service (EBS) as a standalone container.
#
# Build (from the repository root):
#   docker build -f src/EDNexus.Ebs/Dockerfile -t ednexus-ebs .
#
# Run (see .env.example for every setting; the two secrets are required):
#   docker run --env-file src/EDNexus.Ebs/.env -p 127.0.0.1:8787:8787 ednexus-ebs
#
# Or use the compose file next to this one:
#   cd src/EDNexus.Ebs && cp .env.example .env && docker compose up -d
#
# CI publishes this image to ghcr.io/signal-thread-llc/ednexus-ebs.
#
# Broadcaster tokens and encrypted Twitch grants live under /data, and so does the Data Protection
# key ring unless Ebs__DataProtectionKeysDirectory points it elsewhere (the compose file uses /keys).
# Mount a persistent volume there (the compose file does) or every restart logs every broadcaster out.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/EDNexus.Ebs/EDNexus.Ebs.csproj src/EDNexus.Ebs/
RUN dotnet restore src/EDNexus.Ebs/EDNexus.Ebs.csproj

COPY src/EDNexus.Ebs/ src/EDNexus.Ebs/
RUN dotnet publish src/EDNexus.Ebs/EDNexus.Ebs.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# The aspnet image ships neither curl nor wget, so a HEALTHCHECK — and any orchestrator that reads
# one — has nothing to probe /healthz with.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV Ebs__Port=8787
ENV Ebs__DataDirectory=/data
# Created here, owned by the non-root user set below; a volume mounted at /data inherits the
# ownership on first use, so the store can write without running as root.
RUN mkdir -p /data && chown $APP_UID /data
# Mount point for a separate key-ring volume (see docker-compose.yml). Not a VOLUME: a deployment
# that does not set Ebs__DataProtectionKeysDirectory=/keys keeps its keys under /data as before,
# rather than on an anonymous volume that would be lost when the container is recreated.
RUN mkdir -p /keys && chown $APP_UID /keys
VOLUME /data
EXPOSE 8787

# The service validates its Twitch configuration at startup and refuses to run without a valid
# ExtensionSecret, so "the container is up" and "the service works" are genuinely different states.
# Give it room to fail that validation rather than reporting healthy while it crash-loops.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://localhost:${Ebs__Port}/healthz || exit 1

# Drop privileges. The image defines this non-root user, and 8787 is above 1024 so it can still bind.
USER $APP_UID

ENTRYPOINT ["dotnet", "EDNexus.Ebs.dll"]
