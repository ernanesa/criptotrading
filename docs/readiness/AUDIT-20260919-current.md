# Readiness audit — estado corrente

**Data (UTC):** 2026-09-19  
**Commit de código auditado:** `a123e44`
**Escopo:** fundação local, Paper/NO_TRADE e integração Binance read-only  
**Decisão:** **No-Go para Shadow/Canary/Live; Paper local permitido**

## Evidências executadas

Os comandos abaixo foram executados contra o commit de código indicado acima.
O SDK foi chamado pelo caminho local fixado porque o `dotnet` global disponível
na sessão não resolvia o SDK preview declarado em `global.json`.

| Gate | Comando | Resultado |
|---|---|---|
| Testes da solução | `PATH=/home/ernane/.dotnet:$PATH dotnet test CryptoTrading.slnx --configuration Release --no-restore` | PASS — 298 aprovados, 0 falhas, 0 skips |
| Benchmark comparison | `dotnet test tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj --configuration Release --no-restore` | PASS — cash e buy-and-hold obrigatórios, fingerprint determinístico e timeline fail-closed |
| Experiment evidence bundle | `dotnet test tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj --configuration Release --no-restore` | PASS — 3 testes de manifesto, hash, idempotência, redaction e holdout contaminado |
| Worker persistence startup | `./scripts/test-worker-persistence-startup.sh` | PASS — host real, migrations 010–011 e sink PostgreSQL; `PAPER/NO_TRADE`, sem Binance |
| Experiment governance | `dotnet test tests/CryptoTrading.Domain.Tests/CryptoTrading.Domain.Tests.csproj --configuration Release --no-restore` | PASS — 8 testes de preregistration, budget, idempotência e holdout; OOS real continua pendente |
| Experiment governance restart | `./scripts/test-postgres-integration.sh` | PASS — snapshot recomposto após nova instância do store; hash, trial, acesso e contaminação preservados |
| Persistência operacional PostgreSQL | `./scripts/test-postgres-integration.sh` | PASS — 8 aprovados; append idempotente, leitura/hash, colisão e trigger append-only |
| Integração PostgreSQL | `./scripts/test-postgres-integration.sh` | PASS — 9 testes, incluindo migration 011, governança experimental append-only, auditoria de holdout, idempotência, canonicalização JSON, identidade de sessão, projeção de open orders e replay/restart |
| Paper chaos/soak | `./scripts/test-paper-soak-chaos.sh` | PASS — 1 teste, 64 ordens simuladas, sem side effect duplicado |
| Shadow soak determinístico | `./scripts/test-shadow-soak.sh` | PASS — 3 repetições locais, sem rede/credenciais/submit/cancel; relatório redigido em `artifacts/shadow/` |
| Reconstrução determinística do ledger | `./scripts/test-ledger-recovery.sh` | PASS — 3 testes de reconstrução journal → projeção; relatório redigido em `artifacts/recovery/` |
| Backup/restore + RPO/RTO local | `./scripts/test-postgres-backup-restore.sh` | PASS — dump restaurado em banco efêmero; tempos de backup/restore/validação e RPO do marcador registrados em `artifacts/recovery/postgres-rpo-rto-*.md`; recursos removidos |
| Manifesto full-state local | `dotnet test` Persistence filtrado | PASS — 7 testes validam ledger, open orders, Account Stream, outbox metadata, hash canônico e comparação pré/pós-restore; sem escrita/rede |
| Read-only offline preflight | `./scripts/test-binance-readonly-preflight.sh` | PASS — probe recusou sem credenciais, em diretório sem `.env`, com `NO_TRADE=true`/`PAPER`; nenhuma chamada autenticada |
| Secret scan | `./scripts/scan-secrets.sh` | PASS — nenhum padrão de credencial em arquivos versionados |
| Compose | `POSTGRES_PASSWORD='validation-only-not-a-credential' docker compose config --quiet` | PASS |
| Integridade documental | `git diff --check` | PASS |
| Contrato do readiness validator | `./scripts/test-readiness-validator.sh` | PASS — fixture aprovada aceita; checkbox pendente rejeitado; snapshot corrente continua bloqueado |
| API operational status | `dotnet run --configuration Release --no-build --no-launch-profile` + `GET /operational/status` | PASS — estado process-local read-only; `mutatingEndpointsEnabled=false`; nenhuma chamada à Binance |
| RiskPolicy registry | `./scripts/test-postgres-integration.sh` | PASS — migration 006, hash/replay idempotente, aprovação segregada, ativação e supersession; 5 testes PostgreSQL |
| Account stream persistence | `./scripts/test-postgres-integration.sh` | PASS — migrations 007–009, JSON/hash canonicalizado, dedupe idempotente, identidade de sessão, leitura por janela e projeção de open orders; 7 testes PostgreSQL |
| Modern account stream | `dotnet test` Binance + Worker + Reconciliation | PASS — HMAC subscription ACK, parser de controles, reconnect/session identity, rotação preventiva, clock drift, reconciliação pós-reconnect/periódica, filtros dinâmicos e comparação de open orders; 51 + 24 + 11 testes |
| OMS order projection | `dotnet test` Persistence + PostgreSQL integration | PASS — transições idempotentes, preservação de `ExchangeOrderId`, retry após falha do store, estados terminais e replay/restart; 8 unitários + 7 integração |
| Reconciliation mismatch accounting | `dotnet test` Reconciliation + Worker | PASS — `MismatchCount` inclui divergências de saldo e open orders em startup, periodicidade e reconnect; 11 + 24 testes |
| Reconciliation alert delivery | `dotnet test` Worker | PASS — divergência não saudável publica alerta crítico redigido e saudável não publica; startup, periodicidade e reconnect integrados; 26 testes Worker |
| Alert delivery retry | `dotnet test` Observability + Worker | PASS — backoff exponencial limitado, cancelamento não repetido, falha final propagada e wrapper registrado no Worker; 13 + 24 testes |
| Testnet public boundary | `./scripts/test-binance-testnet-public.sh` | PASS — ping, server time, exchangeInfo BTCUSDT e klines BTCUSDT/1d públicos, sem credenciais; cliente fake coberto por 56 testes Binance |
| Public kline recorder | `dotnet run --project tools/CryptoTrading.PublicKlinesRecorder -c Release --no-build -- ...` | PASS — guard de credenciais exit 2 e coleta real limitada a 3 barras; Testnet retornou 2 barras fechadas e manifesto com fingerprint em diretório temporário |
| Public kline quality adapter | `dotnet test tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj --configuration Release --no-restore` | PASS — journal `kline-rest` convertido em `ClosedBarEnvelope` e validado com hash/availability/símbolo; 49 testes Backtesting |
| Public OOS runner | `dotnet run --project tools/CryptoTrading.PublicOosRunner -c Release --no-build -- ...` | PASS — 179 barras, 5 janelas OOS, fingerprint do manifesto igual ao replay reaberto, `promotion_authorized=false`; resultado não é decisão de promoção |
| Public OOS production gate | `./scripts/test-public-oos-production.sh` | PASS — coleta e walk-forward descartáveis reproduzidos com 179 barras/5 janelas; guard de credenciais recusou ambiente contaminado; `promotion_authorized=false` |
| Public OOS CI guard | `./scripts/test-public-oos-production-guard.sh` | PASS — credenciais fictícias foram recusadas antes da rede; o job foi adicionado ao workflow de qualidade |
| External secret-file loading | `dotnet test tests/CryptoTrading.Configuration.Tests/CryptoTrading.Configuration.Tests.csproj --configuration Release --no-restore` | PASS — arquivos externos privados carregam sem ecoar valores; precedência direta e permissões inseguras cobertas por 9 testes |
| Dotenv permission gate | `dotnet test tests/CryptoTrading.Configuration.Tests/CryptoTrading.Configuration.Tests.csproj --configuration Release --no-restore` | PASS — `.env` inseguro/symlink é ignorado em Unix; contrato coberto por 11 testes Configuration |
| Probe external-secret integration | `dotnet build tools/CryptoTrading.BinanceReadOnlyProbe/CryptoTrading.BinanceReadOnlyProbe.csproj --configuration Release --no-restore` | PASS — probe compartilha resolução de arquivos protegidos com o runtime; build sem warnings, sem chamadas mutáveis |
| Governed evidence projection | `dotnet test tests/CryptoTrading.Backtesting.Tests/CryptoTrading.Backtesting.Tests.csproj --configuration Release --no-restore` | PASS — preregistration/dataset/estratégia lineage e trial budget validados antes do bundle; 51 testes Backtesting |
| Governed evidence runner | `dotnet build tools/CryptoTrading.GovernedEvidenceRunner/CryptoTrading.GovernedEvidenceRunner.csproj --configuration Release --no-restore` + guard tests | PASS — snapshot PostgreSQL read-only, sem migrations/ordens; credencial e connection string ausentes falham antes da rede |
| Governed evidence PostgreSQL integration | `./scripts/test-postgres-integration.sh` | PASS — 10/10, incluindo persistência/restart e bundle governado com `promotion_authorized=false`; container efêmero encerrado ao final |
| Governed evidence runner E2E | `./scripts/test-governed-evidence-runner.sh` | PASS — PostgreSQL efêmero persiste fixture governada, runner read-only reconstrói snapshot e produz artefato + manifesto; 179 barras públicas, `promotion_authorized=false`, sem credenciais/ordens |
| Probe autenticado Testnet | `./scripts/test-binance-testnet-auth-readonly.sh` | **PENDING/FAIL EXTERNAL** — após corrigir `/api/v3/time`, a conta autenticada respondeu, mas reportou `canWithdraw=true`; o probe bloqueou fail-closed antes de open orders/trades; nenhuma resposta, chave ou URL assinada foi persistida; requer chave Testnet com retirada desabilitada |
| Testnet probe hardening | `dotnet test` Binance + guard scripts | PASS — endpoint HTTPS Spot Testnet exato, porta 443, ausência de UserInfo/query/fragment, símbolo normalizado e withdrawals false; 56 testes |

| OOS sensitivity local | `./scripts/test-oos-sensitivity.sh` | PASS — 1 teste, três casos preregistrados em seis janelas, execução duplicada e fingerprints estáveis; dataset sintético apenas, sem rede/credenciais |
| Quality gate de dataset externo | `dotnet test` Backtesting | PASS — 3 testes rejeitam look-ahead, sequência duplicada e tempo não monotônico; sem rede/credenciais |
| Webhook de alertas opt-in | `dotnet test` Observability | PASS — 6 testes cobrem desabilitado, allowlist HTTPS, redaction e não-2xx usando handler fake; nenhum endpoint real chamado |
| Integração Worker de webhook | `dotnet test` Worker | PASS — configuração URL/allowlist/timeout validada fail-closed; default local preservado; nenhuma chamada externa realizada |
| Evidência pós-reconciliação redigida | `dotnet test` Worker filtrado | PASS — contrato rejeita timestamps não-UTC, modo mutável, contagens inconsistentes e relatório saudável com drift; nenhuma credencial/payload é persistido |
| Shadow operational quality gate | `./scripts/test-shadow-operational-gate.sh` | PASS — métricas locais de divergência, stale, recovery, intervention-rate e duração avaliadas com thresholds explícitos; sem rede/credenciais/ordens |
| Validade de evidência de promoção | `./scripts/test-readiness-validator.sh` | PASS — datas inválidas/expiradas são rejeitadas; auditorias podem fixar `READINESS_AS_OF_UTC`; aprovação humana continua obrigatória |
| Validade do CanaryPlan | `./scripts/test-readiness-validator.sh` | PASS — CanaryPlan exige data UTC válida e vigente; datas inválidas/expiradas falham fechado |
| Permissões de evidência local | testes Observability/Shadow/Worker | PASS — arquivos JSONL redigidos são restringidos a `0600` em Unix; conteúdo continua sem credenciais/payloads |
| OMS partial-fill/idempotency | `dotnet test` Domain | PASS — fills parciais, replay idempotente, duplicata conflitante, overfill, ordem temporal invertida e cancelamento pós-fill cobertos; sem rede/ordens |
| Release evidence local | `dotnet test` Shadow filtrado | PASS — artefato canônico redigido valida janela, decisão, responsáveis, fontes, métricas, follow-ups e hash; rejeita metadados com aparência de segredo |
| Audit do controle operacional | `dotnet test` Domain filtrado | PASS — transições de KillSwitch/SafeMode persistem evento versionado/hash antes do estado; falha do sink mantém estado inalterado |

## O que estes gates comprovam

- OOS sintético, walk-forward determinístico, property tests financeiros,
  simulador Paper, Monte Carlo/stress e controles de risco estão cobertos pela
  suite automatizada.
- Crash recovery de leases e idempotência PostgreSQL são cobertos pelo teste de
  integração dedicado; o backup/restore isolado foi executado nesta auditoria.
- O Paper chaos/soak foi executado sem rede, PostgreSQL, credenciais ou ordens.
- O probe autenticado permanece read-only e falha fechado sem credenciais.
- O avaliador local `ShadowDriftEvaluator` calcula divergência, stale e recovery
  de observações redigidas sem qualquer capacidade de submissão de ordem.
- O soak determinístico local foi executado com 64 observações, divergência
  sintética, janelas stale e recovery; o runner repetiu três janelas, agregou os
  resumos e falhou fechado para credenciais presentes. O relatório está em
  `artifacts/shadow/` e não substitui o Shadow operacional com dados reais.
- O `ShadowOperationalGate` valida thresholds explícitos para divergência, stale,
  recovery, intervention-rate e duração; esse resultado é somente um gate local.
- A evidência pós-reconciliação é redigida e validada antes da persistência,
  rejeitando modo mutável, timestamps não-UTC e estados inconsistentes.
- O Worker Shadow local persistiu relatório JSONL redigido e publicou alertas
  deduplicados de divergência, stale e recovery; canais externos ainda não
  foram configurados.
- O Worker possui um sink JSONL local opt-in para alertas redigidos,
  `WORKER_ALERT_FILE_DIRECTORY`; o sink em memória continua sendo o padrão e
  nenhum canal externo é presumido.
- A camada Binance possui ingestão de User Data Stream read-only para
  `executionReport` e `outboundAccountPosition`, com parser, reconnect e
  backoff testáveis. O lifecycle REST de criação/renovação/fechamento do
  `listenKey` também possui cliente restrito e testes fake; conectividade
  Testnet real e projeção durável dos eventos continuam pendentes.
- O Worker possui uma reconciliação autenticada read-only opt-in no startup,
  condicionada a PostgreSQL local e `NO_TRADE`; sem credenciais novas ela falha
  antes de qualquer chamada. A execução contra a conta Testnet ainda não foi
  realizada.
- A migration 005 persiste IDs externos conhecidos de forma append-only e o
  Worker os lê do PostgreSQL para a reconciliação; conflitos de símbolo para o
  mesmo ID falham de forma explícita.
- A fronteira autenticada rejeita contas com withdrawals habilitados ou
  permissões fora da allowlist read-only; a coleta de trades pagina por
  `fromId`, deduplica IDs e falha fechado ao atingir o limite configurado.
- A reconciliação compara a união dos ativos locais e externos; um ativo
  presente apenas em um dos lados gera divergência e entrada em `SafeMode`.
- O vínculo entre evento contábil e `trade_id` externo possui uma operação
  transacional única, com replay idempotente e conflito de símbolo fail-closed.

## Gates que continuam pendentes

- dataset de mercado real com OOS/walk-forward/sensitivity e critérios
  quantitativos aprovados;
- soak Shadow com período, volume, regimes, recovery e intervention-rate
  medidos em dados reais;
- execução autenticada read-only contra uma nova chave Testnet, com relatório
  redigido de balances, open orders e trades e reconciliação do ledger;
- alert delivery real, RPO/RTO de produção e reconstrução completa de estado;
- RiskPolicy, PRM e CanaryPlan preenchidos com valores/unidades, validade e
  aprovação humana;
- o snapshot corrente também falha fechado no `scripts/validate-readiness.sh`
  enquanto RiskPolicy e aprovações humanas não estiverem preenchidas;
- o registry de RiskPolicy agora garante a trilha técnica de hash, validade,
  aprovação e ativação, mas não substitui a aprovação humana dos thresholds;
- qualquer submissão de ordem, mesmo em Testnet, continua proibida até decisão
  explícita e independente.

## Regra de interpretação

`PASS` nesta auditoria significa somente que o gate local indicado foi
executado. Não converte evidência sintética em evidência de mercado, não prova
paridade com a Binance e não autoriza promoção nem capital.
