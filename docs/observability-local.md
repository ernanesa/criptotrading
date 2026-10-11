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

## Consulta local de logs e exercício seguro

Este procedimento é somente de leitura local: não cria alertas externos, não
envia ordens, não consulta a exchange e não copia logs brutos para artefatos.
Antes de executá-lo, remova credenciais Binance do ambiente do terminal; os
containers não devem ser reiniciados ou reconfigurados como parte do exercício.

```bash
env -u BINANCE_API_KEY -u BINANCE_API_SECRET \
  docker compose logs --no-color --tail 200 cryptotrading-worker cryptotrading-api | \
  rg -i 'safemode|killswitch|reconciliation|unknown order|dead.?letter|error|critical'
```

Para validar o limite independente de risco sem rede, credenciais ou ordens,
execute o drill local:

```bash
env -u BINANCE_API_KEY -u BINANCE_API_SECRET bash scripts/test-operational-control.sh
```

O exercício é observável quando: o comando de consulta só lê as últimas 200
linhas dos serviços locais; cada ocorrência investigada pode ser correlacionada
por `X-Correlation-Id` ou `correlation_id`; e o drill termina com `PASS`,
registrando um relatório `0600` em `artifacts/operations/`. Uma ocorrência de
`SafeMode`, `KillSwitch`, divergência de reconciliação, `unknown order` ou
dead-letter exige registrar o correlation ID, horário UTC e ação no runbook
local antes de qualquer tentativa de retomada.

Não cole URI com query, header `Authorization`, bearer token, password ou
credencial em propriedades de diagnóstico. Quando uma propriedade de
diagnóstico contiver esses formatos, `SecretRedactor` mascara valores de query
sensíveis, credenciais em URI e valores de Authorization/Bearer; isso é defesa
complementar e não autoriza registrar o valor original.

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

## Regras de alerta Prometheus e exercício de drill

O repositório define regras determinísticas de alerta em `ops/observability/prometheus-alerts.yml` e configuração do Alertmanager em `ops/observability/alertmanager.yml`. As 4 condições essenciais de falha operacional são monitoradas:

1. **Backup Failure / Staleness (`CryptoTradingBackupStaleOrFailed`)**:
   - Disparado se o backup agendado do PostgreSQL não for concluído em até 900s (RPO de 15m) ou registrar falha na última execução (`cryptotrading_backup_last_run_failed > 0`). Severidade: `critical`.
2. **Espaço em disco baixo (`CryptoTradingLowDiskSpace`)**:
   - Disparado se o espaço livre em disco ficar abaixo de 2 GB (`2147483648` bytes) ou a fração livre for inferior a 10% (`0.10`), acionando fail-closed. Severidade: `critical`.
3. **Perda de heartbeat do Worker (`CryptoTradingWorkerHeartbeatMissing`)**:
   - Disparado se nenhum heartbeat do Worker for observado em 10m (`rate == 0` ou métrica ausente). Severidade: `warning`.
4. **Divergência de reconciliação e SafeMode (`CryptoTradingReconciliationMismatch` / `CryptoTradingSafeModeActivated`)**:
   - Disparado imediatamente em caso de mismatch de reconciliação ou ativação de SafeMode. Severidade: `critical`.

### Execução do drill local de alertas

Para validar a integridade das regras Prometheus, do Alertmanager e testar ponta a ponta a geração de alertas, ingestão em JSONL (`0600`) e webhook local loopback com query verification:

```bash
bash scripts/test-observability-alert-drill.sh
```

O drill opera 100% offline (loopback `127.0.0.1`), não emite requisições para a nuvem, não lê credenciais e salva evidência auditável em `artifacts/operations/observability-alert-drill-*.md`.
