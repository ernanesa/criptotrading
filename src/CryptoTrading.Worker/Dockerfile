# Multi-stage Dockerfile for CryptoTrading.Worker (US-120)
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /app

# Copy solution and project files for layer caching
COPY Directory.Build.props ./
COPY src/CryptoTrading.Domain/*.csproj ./src/CryptoTrading.Domain/
COPY src/CryptoTrading.Configuration/*.csproj ./src/CryptoTrading.Configuration/
COPY src/CryptoTrading.Observability/*.csproj ./src/CryptoTrading.Observability/
COPY src/CryptoTrading.Persistence/*.csproj ./src/CryptoTrading.Persistence/
COPY src/CryptoTrading.Exchange.Binance/*.csproj ./src/CryptoTrading.Exchange.Binance/
COPY src/CryptoTrading.Exchange.Paper/*.csproj ./src/CryptoTrading.Exchange.Paper/
COPY src/CryptoTrading.Shadow/*.csproj ./src/CryptoTrading.Shadow/
COPY src/CryptoTrading.Reconciliation/*.csproj ./src/CryptoTrading.Reconciliation/
COPY src/CryptoTrading.Backtesting/*.csproj ./src/CryptoTrading.Backtesting/
COPY src/CryptoTrading.Worker/*.csproj ./src/CryptoTrading.Worker/

RUN dotnet restore src/CryptoTrading.Worker/CryptoTrading.Worker.csproj

# Copy source code and publish
COPY src/ ./src/
RUN dotnet publish src/CryptoTrading.Worker/CryptoTrading.Worker.csproj -c Release -o /out --no-restore

# Final runtime image
FROM mcr.microsoft.com/dotnet/runtime:11.0-preview-alpine AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_ENVIRONMENT=Production

RUN addgroup -S appgroup && adduser -S appuser -G appgroup
USER appuser

COPY --from=build --chown=appuser:appgroup /out ./

ENTRYPOINT ["dotnet", "CryptoTrading.Worker.dll"]
