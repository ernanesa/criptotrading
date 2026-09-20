# Observabilidade local

O runtime local expõe um baseline sem dependência de cloud:

- logs JSON no console da API e do Worker;
- `X-Correlation-Id` propagado na resposta HTTP e incluído no escopo do log;
- liveness em `GET /health/live`;
- readiness em `GET /health/ready`, que valida configuração mínima do Testnet sem revelar credenciais;
- snapshot local em `GET /metrics` com requests, erros 5xx, latência acumulada e heartbeats do Worker;
- métricas de reconciliação (execuções e divergências), despacho da outbox (processado/retry/dead-letter) e atraso de fila;
- contador de payloads de market data rejeitados antes de alcançarem estratégia, para detectar incompatibilidade de schema ou feed degradado;
- `Meter` e `ActivitySource` em `CryptoTrading.Runtime`, ligados ao SDK
  OpenTelemetry de forma opcional no API e Worker.
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

## Collector OpenTelemetry local

Por padrão, nenhum exporter OTLP é criado e nenhum tráfego de telemetria sai da
máquina. Para um collector local, configure no `.env`:

```dotenv
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_EXPORTER_OTLP_ALLOWED_HOSTS=
```

Também é aceito `http/protobuf` com a porta `4318`. Endpoints sem loopback
exigem `OTEL_EXPORTER_OTLP_ALLOWED_HOSTS` explícita; userinfo, query string,
fragmentos e portas incompatíveis com o protocolo são rejeitados no startup.
Essa configuração exporta somente os instrumentos `CryptoTrading.Runtime` e a
`ActivitySource` correspondente; mantém os logs JSON e o snapshot local
`/metrics`. O projeto não inicia collector, não provisiona cloud e não envia
credenciais ou payloads de exchange.

Exemplo de rollout local:

1. iniciar um collector local ouvindo apenas `127.0.0.1:4317` ou `:4318`;
2. preencher as variáveis acima sem adicionar valores ao Git;
3. iniciar API/Worker em `PAPER`/`NO_TRADE`;
4. verificar o collector e comparar a telemetria com `/metrics` e os logs JSON;
5. remover `OTEL_EXPORTER_OTLP_ENDPOINT` para retornar ao modo sem exporter.

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
