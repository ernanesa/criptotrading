# Observabilidade local

A observabilidade é um plano separado do caminho de ordens. Se Collector, Prometheus, Grafana ou Tempo estiverem indisponíveis, o worker não deve liberar operações que as políticas de risco bloqueariam nem alterar seu comportamento financeiro.

## Inicialização

```bash
docker compose -f compose.observability.yaml config
docker compose -f compose.observability.yaml up -d
```

Endpoints locais:

- Collector OTLP gRPC: `127.0.0.1:4317`
- Collector OTLP HTTP: `127.0.0.1:4318`
- Prometheus: `http://127.0.0.1:9090`
- Grafana: `http://127.0.0.1:3000`
- Tempo: `http://127.0.0.1:3200`

O worker/API deve receber `OTEL_EXPORTER_OTLP_ENDPOINT` apontando para o Collector somente quando o Collector estiver disponível. Os hosts permitidos continuam sujeitos à validação existente da aplicação.

As imagens são parametrizadas por variáveis de ambiente para permitir atualização controlada, mas possuem versões padrão fixadas. Antes de atualizar uma imagem, executar `docker compose ... config`, os testes locais e uma inspeção de compatibilidade.

Para validar uma segunda instância sem conflitar com outros serviços locais, altere as portas publicadas, por exemplo `GRAFANA_PORT=13000 PROMETHEUS_PORT=19090 docker compose ... up -d`.
