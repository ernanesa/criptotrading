# Multi-stage Dockerfile for CryptoTrading.Worker (US-120).
# Official multi-platform digests inspected 2026-10-07; SDK aligned with global.json.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1@sha256:78559d13d8c810bfe87f329168739ae2ee31f402b1e2c66353d3ff9744d68520 AS build
WORKDIR /app

# Copy source before locked restore so generated packages.lock.json files are
# available to the image without copying repository-local credentials.
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/ ./src/

RUN dotnet restore src/CryptoTrading.Worker/CryptoTrading.Worker.csproj --locked-mode

RUN dotnet publish src/CryptoTrading.Worker/CryptoTrading.Worker.csproj -c Release -o /out --no-restore

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1@sha256:68d8e392378201205434075873ef9d436e26031e81f446bf884b62b0956297ec AS runtime
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
