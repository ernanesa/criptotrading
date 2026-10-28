# Multi-stage Dockerfile for CryptoTrading.Worker (US-120).
# Official multi-platform digests inspected 2026-10-28; SDK aligned with global.json.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /app

# Copy source before locked restore so generated packages.lock.json files are
# available to the image without copying repository-local credentials.
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/ ./src/

RUN dotnet restore src/CryptoTrading.Worker/CryptoTrading.Worker.csproj --locked-mode

RUN dotnet publish src/CryptoTrading.Worker/CryptoTrading.Worker.csproj -c Release -o /out --no-restore

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045 AS runtime
WORKDIR /app

ARG CRYPTO_UID=1000
ARG CRYPTO_GID=1000

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_ENVIRONMENT=Production

RUN mkdir -p /var/lib/cryptotrading/audit \
    && chown "$CRYPTO_UID:$CRYPTO_GID" /var/lib/cryptotrading/audit \
    && chmod 0700 /var/lib/cryptotrading/audit
USER ${CRYPTO_UID}:${CRYPTO_GID}

COPY --from=build --chown=${CRYPTO_UID}:${CRYPTO_GID} /out ./

ENTRYPOINT ["dotnet", "CryptoTrading.Worker.dll"]
