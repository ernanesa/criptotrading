# Observabilidade local

O runtime local expõe um baseline sem dependência de cloud:

- logs JSON no console da API e do Worker;
- `X-Correlation-Id` propagado na resposta HTTP e incluído no escopo do log;
- liveness em `GET /health/live`;
- readiness em `GET /health/ready`, que valida configuração mínima do Testnet sem revelar credenciais;
- snapshot local em `GET /metrics` com requests, erros 5xx, latência acumulada e heartbeats do Worker;
- `Meter` e `ActivitySource` em `CryptoTrading.Runtime` para futura exportação OpenTelemetry.

Os instrumentos têm dimensões de baixa cardinalidade. IDs de ordens, chaves, secrets, tokens e payloads de exchange não devem ser adicionados como labels ou propriedades de log. A classe `SecretRedactor` fornece uma proteção explícita para valores sensíveis, mas a regra principal é não registrar credenciais.

## Validação

Com a configuração local preenchida em `.env`:

```bash
export PATH="$PWD/.tools/dotnet:$HOME/.dotnet:$PATH"
dotnet run --project src/CryptoTrading.Api --urls http://127.0.0.1:5080
curl -i http://127.0.0.1:5080/health/live
curl -i http://127.0.0.1:5080/health/ready
curl -s http://127.0.0.1:5080/metrics
```

O endpoint `/metrics` é um diagnóstico local em JSON, não substitui um exporter Prometheus. A próxima evolução deve conectar `TradingTelemetry.Meter` e `TradingTelemetry.ActivitySource` ao OpenTelemetry Collector local, preservando a política de redaction e retenção definida no plano.
