# Observabilidade local

O runtime local expõe um baseline sem dependência de cloud:

- logs JSON no console da API e do Worker;
- `X-Correlation-Id` propagado na resposta HTTP e incluído no escopo do log;
- liveness em `GET /health/live`;
- readiness em `GET /health/ready`, que valida configuração mínima do Testnet sem revelar credenciais;
- snapshot local em `GET /metrics` com requests, erros 5xx, latência acumulada e heartbeats do Worker;
- `Meter` e `ActivitySource` em `CryptoTrading.Runtime` para futura exportação OpenTelemetry.
- `TradingAlertDispatcher` com envelope operacional obrigatório, deduplicação por
  chave e `InMemoryTradingAlertSink` para drills/testes determinísticos locais.

O sink em memória comprova apenas que o runtime produz e entrega o envelope ao
componente configurado. Ele não comprova entrega a um canal externo ou cobertura
de on-call. Uma integração externa deve preservar `environment`, `exchange`,
`scope`, timestamp UTC, correlation ID, severidade, owner, ação e runbook, sem
enviar credenciais ou payloads privados.

Existe um `HttpWebhookTradingAlertSink` opt-in para a próxima etapa
operacional. Ele permanece desabilitado por padrão e somente aceita endpoint
HTTPS na porta 443, sem `userinfo`, query string ou fragmento, quando o host
também estiver em uma allowlist explícita. O payload é exclusivamente o
envelope `TradingAlert`; não são adicionados headers de autenticação, API keys
ou tokens. Configuração inválida falha na instanciação, e o sink não faz
chamadas quando desabilitado. Os testes usam `HttpMessageHandler` fake; nenhum
webhook real faz parte da validação local.

No Worker, `WORKER_ALERT_WEBHOOK_URL` vazio mantém o sink em memória ou JSONL.
Quando preenchido, `WORKER_ALERT_WEBHOOK_ALLOWED_HOSTS` é obrigatório e o
Worker falha no startup se o endpoint não for HTTPS/443, contiver userinfo,
query/fragmento ou não estiver na allowlist. O timeout é limitado a 120
segundos. Essa configuração apenas habilita o adaptador; a entrega real e o
teste de on-call continuam exigindo um endpoint operacional aprovado.

Os arquivos JSONL locais de alertas, Shadow e reconciliação são restringidos
explicitamente a `0600` em sistemas Unix, independentemente do `umask` do
processo. Isso reduz o risco de evidência operacional redigida ser exposta a
outros usuários da máquina; o conteúdo continua sem credenciais ou payloads
privados.

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

`/metrics` e `/operational/status` recusam conexões que não sejam loopback,
mesmo que o processo seja iniciado com bind externo por engano. Para publicar
diagnósticos fora da máquina, a evolução obrigatória é uma superfície separada
com autenticação, autorização e política de rede explícitas.
