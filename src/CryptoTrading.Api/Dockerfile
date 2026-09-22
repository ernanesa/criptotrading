# Multi-stage Dockerfile for CryptoTrading.Api (US-120)
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /app

# Copy solution and project files for layer caching
COPY Directory.Build.props ./
COPY src/CryptoTrading.Primitives/*.csproj ./src/CryptoTrading.Primitives/
COPY src/CryptoTrading.Domain/*.csproj ./src/CryptoTrading.Domain/
COPY src/CryptoTrading.Configuration/*.csproj ./src/CryptoTrading.Configuration/
COPY src/CryptoTrading.Observability/*.csproj ./src/CryptoTrading.Observability/
COPY src/CryptoTrading.Persistence/*.csproj ./src/CryptoTrading.Persistence/
COPY src/CryptoTrading.Exchange.Binance/*.csproj ./src/CryptoTrading.Exchange.Binance/
COPY src/CryptoTrading.Reconciliation/*.csproj ./src/CryptoTrading.Reconciliation/
COPY src/CryptoTrading.Api/*.csproj ./src/CryptoTrading.Api/

RUN dotnet restore src/CryptoTrading.Api/CryptoTrading.Api.csproj

# Copy source code and publish
COPY src/ ./src/
RUN dotnet publish src/CryptoTrading.Api/CryptoTrading.Api.csproj -c Release -o /out --no-restore

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:11.0-preview-alpine AS runtime
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production

RUN addgroup -S appgroup && adduser -S appuser -G appgroup
USER appuser

COPY --from=build --chown=appuser:appgroup /out ./

ENTRYPOINT ["dotnet", "CryptoTrading.Api.dll"]
