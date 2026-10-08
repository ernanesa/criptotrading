# Multi-stage Dockerfile for CryptoTrading.Web (US-120)
FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS build
WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm ci

COPY . ./
RUN npm run build

# Official multi-platform image digests inspected on 2026-10-07; verify security scans before promotion.
FROM nginxinc/nginx-unprivileged:1.30.5-alpine@sha256:15c994d10d6d78658721c3bcafff14cb281fba2a4bdf9d5ba92c416a472516e3 AS runtime
ARG CRYPTO_UID=1000
ARG CRYPTO_GID=1000
USER root
RUN mkdir -p /run/secrets && chmod 700 /run/secrets && chown ${CRYPTO_UID}:${CRYPTO_GID} /run/secrets
RUN chown -R "$CRYPTO_UID:$CRYPTO_GID" /etc/nginx /var/cache/nginx /usr/share/nginx/html
COPY --chown=${CRYPTO_UID}:${CRYPTO_GID} --from=build /app/dist /usr/share/nginx/html
COPY --chown=${CRYPTO_UID}:${CRYPTO_GID} nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --chown=${CRYPTO_UID}:${CRYPTO_GID} --chmod=755 nginx/30-api-internal-header.sh /docker-entrypoint.d/30-api-internal-header.sh
USER ${CRYPTO_UID}:${CRYPTO_GID}
EXPOSE 8080

CMD ["nginx", "-g", "daemon off;"]
