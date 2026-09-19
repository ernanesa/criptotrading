# Checklist de implementação

Use este arquivo para acompanhar o roadmap local-first. Marque `[x]` somente
quando houver teste, comando, evidência ou revisão registrada. Use `[~]` para
itens em andamento ou bloqueados.

## Fase 0 — Bootstrap e governança

- [x] Solução .NET 11/C# 15 e SDK fixado em `global.json`.
- [x] API e Worker mínimos criados.
- [x] Health checks básicos implementados.
- [x] Build local validado com SDK .NET 11 RC.
- [x] `.gitignore` protege `.env`, chaves, logs e artefatos.
- [x] `.env.example` documenta o contrato local de Testnet.
- [x] `.env` local criado, ignorado pelo Git e com permissão `600`.
- [x] Teste público da Binance Testnet executado.
- [x] CI executa restore, build, testes, análise, formatação e secret scan sem imprimir correspondências.
- [x] Validator executável de rastreabilidade valida schema/IDs/referências e está conectado à quality gate.

## Fase 1 — Domínio e contratos

- [x] Adicionar `TimeProvider` injetável e relógio determinístico.
- [x] Criar IDs de domínio, correlation ID e idempotency key.
- [x] Criar tipos de dinheiro, preço, quantidade e percentual com `decimal`.
- [x] Modelar estados de ordem, fill e transições explícitas do ciclo de vida.
- [x] Definir contratos Strategy → Portfolio → Risk → Execution → OMS → Exchange.
- [x] Criar testes unitários básicos para primitives e relógio.
- [x] Adicionar testes de arquitetura para fronteiras do domínio.
- [x] Adicionar property-based tests financeiros determinísticos.

## Fase 2 — Persistência financeira local

- [x] Adicionar PostgreSQL 18 ao Docker Compose, com volume nomeado, bind local e healthcheck.
- [x] Criar migrations SQL versionadas, rollback explícito e política de backup local.
- [x] Implementar journal imutável e ledger de saldos, fees, posições e PnL (contrato in-memory e store PostgreSQL transacional).
- [x] Implementar outbox/inbox com lease, retry e deduplicação.
- [x] Registrar o store no Worker sob flag explícita e aplicar migrations sob flag explícita.
- [x] Implementar dispatcher PAPER com lease, retry, logs sem payload e default `NO_TRADE`.
- [x] Implementar readiness real para banco e dependências.
- [x] Validar restart, crash recovery de leases, idempotência durável e reconstrução determinística local; o gate composto executa ledger, persistência/restart e backup/restore, mede RPO/RTO do marcador e mantém o RPO/RTO de produção pendente.

## Fase 3 — Binance read-only e market data

- [x] Implementar adapter REST read-only (`ping`, `time`, `exchangeInfo`).
- [x] Validar ambiente, endpoint e permissões fail-closed.
- [x] Implementar rate limits, retry, backoff e clock drift.
- [x] Implementar `exchangeInfo`, filtros e precisão por símbolo.
- [x] Implementar trades/candles via WebSocket e reconexão pública resiliente.
- [x] Implementar order book local, gap detection e resync.
- [x] Implementar recorder/replay de dados públicos selecionados com envelope versionado, checksum, retenção explícita e ordenação determinística.

## Fase 4 — OMS, reconciliação e risco

- [x] Implementar máquina de estados de ordens e `clientOrderId` idempotente.
- [x] Garantir persist-before-submit e tratamento de timeout ambíguo.
- [x] Implementar reconciliação read-only de saldos/fills com entrada em SafeMode em caso de drift.
- [x] Implementar `RiskPolicy` versionada.
- [x] Persistir `RiskPolicy` com hash canônico, validade, aprovação segregada e lifecycle append-only; gate de startup permanece opt-in.
- [x] Implementar limites de exposição, notional, daily loss e drawdown.
- [x] Implementar controles de stale data e bloqueio de withdrawals.
- [x] Implementar slippage, volatilidade e liquidez com dados de mercado locais, com limites fail-closed.
- [x] Implementar kill switch, SafeMode, reset autorizado e testes de falha.

## Fase 5 — Integração autenticada Testnet

- [ ] Revogar chaves expostas e gerar nova chave Spot Testnet.
- [ ] Confirmar withdrawals, transferências, margem e Futures desativados.
- [ ] Preencher credenciais somente no `.env` local ou secret store externo.
- [x] Implementar adapter autenticado read-only para account, balances, open orders e trades (sem submit/cancel).
- [x] Integrar reconciliação read-only opcional no startup do Worker, exigindo persistência + `NO_TRADE` e ativando `SafeMode` para drift/stale; execução autenticada real permanece aguardando chaves novas.
- [ ] Executar primeiro teste de ordem com notional mínimo e Risk aprovado.
- [ ] Registrar evidência redacted e confirmar reconciliação após restart.

## Fase 6 — Paper, replay e estratégia baseline

- [x] Implementar paper exchange determinística.
- [x] Versionar metadata de dataset, configuração, seed e timezone do replay.
- [x] Implementar contratos de replay/backtest reproduzível.
- [x] Especificar baseline Trend Following BTC/USDT.
- [x] Cobrir fees e slippage explícitos na baseline.
- [x] Implementar slice determinístico com equity, retorno líquido, fees, turnover, drawdown e Sharpe parametrizado.
- [x] Adicionar contratos de janelas out-of-sample/walk-forward e matriz de sensibilidade.
- [x] Executar validação OOS com dataset sintético versionado, comparar janelas walk-forward e percorrer matriz preregistrada de sensibilidade; dataset de mercado real e sensibilidade empírica de execução permanecem pendentes.
- [x] Evoluir o simulador para spread, latência, partial fills, rejeições e limites; impacto de mercado permanece pendente.
- [x] Implementar Monte Carlo e stress tests determinísticos depois de validar o modelo de execução.

## Fase 7 — Shadow, observabilidade e operação

- [x] Implementar Strategy Health, estados `Healthy`/`NoTrade`/`Blocked` e razões auditáveis.
- [x] Instrumentar logs JSON, correlação, métricas e `ActivitySource` local.
- [x] Criar alertas, redaction, dashboards e runbooks (baseline local; alertas remotos ainda pendentes).
- [x] Criar threat model local.
- [~] Executar backup/restore, chaos e soak test em shadow; backup/restore, Paper chaos e soak Shadow determinístico local validados, Shadow operacional com dados reais ainda pendente.

## Fase 8 — Readiness e canary

- [ ] Aprovar `RiskPolicy`.
- [x] Preencher snapshots locais de `PromotionReadinessMatrix` e `CanaryPlan`; aprovação continua pendente.
- [x] Preencher snapshots locais de `ThreatModel`, `ReconciliationReport` e `ReleaseEvidence`.
- [x] Vincular critérios de aceitação ao manifesto de rastreabilidade local.
- [x] Realizar revisão independente de Risk/Execution; achados P1 corrigidos, P2 de dead-letter implementado.
- [ ] Aprovar Go/No-Go e executar canary sem capital.

## Registro de execução

| Data | Item | Resultado | Evidência | Commit | Responsável |
|---|---|---|---|---|---|
| 2026-09-19 | Bootstrap | Build .NET 11: 0 warnings/0 errors | `dotnet build --no-restore` | `7be8714` | Codex |
| 2026-09-19 | Binance pública | `ping`, `time`, `exchangeInfo` aprovados | `scripts/test-binance-testnet-public.sh` | `828a449` | Codex |
| 2026-09-19 | CI/gates | Restore, build Release, 9 testes, format e secret scan aprovados; Compose requer `POSTGRES_PASSWORD` explícita | `.github/workflows/quality.yml`, `scripts/scan-secrets.sh` | `64b333c` | Codex |
| 2026-09-19 | Traceability validator | Manifesto v2 validado com 115 IDs explícitos; fixture duplicada falha fechado; references AC/RF/RNF e tasks são verificadas contra fontes canônicas | `scripts/validate-traceability.sh`, `plan/traceability.yaml`, `.github/workflows/quality.yml` | validação local | Codex |
| 2026-09-19 | Domínio | Primitives e relógio determinístico; build sem warnings e 9 testes aprovados | `src/CryptoTrading.Domain/Primitives`, `tests/CryptoTrading.Domain.Tests` | `319b4fc` | Codex |
| 2026-09-19 | PostgreSQL local | Compose validado com senha temporária, bind em loopback e healthcheck | `compose.yaml`, `docs/postgres-local.md` | `66e982d` | Codex |
| 2026-09-19 | Contabilidade | Journal/ledger in-memory com idempotência, balanceamento e atomicidade; 15 testes aprovados | `src/CryptoTrading.Domain/Accounting`, testes de domínio | `50cba3b` | Codex |
| 2026-09-19 | Configuração | `.env` carregado localmente e validação Testnet fail-closed; 5 testes aprovados | `src/CryptoTrading.Configuration` | `9a11432` | Codex |
| 2026-09-19 | Binance read-only | Cliente REST público e autenticado read-only com validação de permissões; 42 testes aprovados | `src/CryptoTrading.Exchange.Binance` | `005355e` | Codex |
| 2026-09-19 | Persistência SQL | Migrations journal/ledger/outbox e trade IDs conhecidos, vínculo atômico journal/ledger/trade, guards append-only e rollback documentado; 2 testes unitários + 4 de integração aprovados | `src/CryptoTrading.Persistence`, `src/CryptoTrading.Persistence/Migrations/005_reconciliation_known_trades.up.sql`, `docs/persistence-migrations.md` | `a5b3f62` + validação local | Codex |
| 2026-09-19 | Market data | Streams públicos trade/kline com parsing, fragmentação, reconnect/backoff; 10 testes aprovados | `src/CryptoTrading.Exchange.Binance`, `docs/market-data-websocket.md` | `49a3abc` | Codex |
| 2026-09-19 | User Data Stream | Eventos `executionReport`/`outboundAccountPosition`, listen key recebido externamente, reconnect/backoff e parser fail-closed; testes fake, sem lifecycle de chave ou conectividade real | `src/CryptoTrading.Exchange.Binance/BinanceUserDataStream.cs`, `docs/market-data-websocket.md` | `34705af` + validação local | Codex |
| 2026-09-19 | User Data Stream lifecycle | Criação, renovação e fechamento restritos a `v3/userDataStream`, host Testnet/Demo allowlisted, API key header e testes HTTP fake; sem endpoints de ordens | `src/CryptoTrading.Exchange.Binance/BinanceUserDataStream.cs`, `src/CryptoTrading.Exchange.Binance/ServiceCollectionExtensions.cs`, `docs/market-data-websocket.md` | validação local | Codex |
| 2026-09-19 | Modern account stream | HMAC `userDataStream.subscribe.signature`, ACK/event consumption on one session, control-event parsing, deterministic dedupe, durable session identity, proactive 23h rotation, clock-drift-aware signing, reconciliação pós-reconnect and SafeMode gates; 50 Binance + 23 Worker tests | `src/CryptoTrading.Exchange.Binance/BinanceUserDataStreamApi.cs`, `src/CryptoTrading.Worker/AccountStreamHostedService.cs`, `src/CryptoTrading.Persistence/Migrations/008_account_stream_session_identity.up.sql`, `docs/market-data-websocket.md` | `a11c73e`, `098d276` + validation local | Codex |
| 2026-09-19 | Periodic reconciliation | Reconciliation read-only no startup, reconnect e janela periódica configurável; relatório redigido e SafeMode em falha inesperada; 24 testes Worker | `src/CryptoTrading.Worker/PeriodicReconciliationHostedService.cs`, `.env.example` | validação local | Codex |
| 2026-09-19 | Dynamic Binance symbol filters | Parser exact-decimal para `MIN_NOTIONAL`/`NOTIONAL`, maximum notional e preservação de filtros desconhecidos; 51 testes Binance | `src/CryptoTrading.Exchange.Binance/BinanceSymbolTradingRules.cs`, `src/CryptoTrading.Exchange.Binance/BinanceModels.cs` | validação local | Codex |
| 2026-09-19 | Reconciliation open-order evidence | Snapshot autenticado preserva `orderId`, `clientOrderId`, símbolo e status de open orders por símbolo; relatório local registra a contagem sem expor payload; 10 testes Reconciliation + Worker | `src/CryptoTrading.Reconciliation/ReconciliationContracts.cs`, `src/CryptoTrading.Worker/ReconciliationReportWriter.cs` | validação local | Codex |
| 2026-09-19 | Local open-order projection | Migration 009, reader/upsert idempotente PostgreSQL e comparação fail-closed de ordens locais vs. exchange por `clientOrderId`; drift de identidade/status bloqueia novas entradas; 11 testes Reconciliation | `src/CryptoTrading.Persistence/Migrations/009_reconciliation_open_orders.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `src/CryptoTrading.Reconciliation/ReconciliationContracts.cs` | validação local | Codex |
| 2026-09-19 | Open-order projection replay/restart | Integração PostgreSQL verifica upsert repetido, leitura da última identidade/status após restart, remoção durável e recriação por snapshot posterior; 7 testes de integração verdes | `tests/CryptoTrading.Persistence.IntegrationTests/PostgresTradingStoreIntegrationTests.cs`, `docs/persistence-migrations.md`, `scripts/test-postgres-integration.sh` | `./scripts/test-postgres-integration.sh` | Codex |
| 2026-09-19 | OMS projection integration | Serviço de projeção aplica transições do agregado, mantém `ExchangeOrderId`, suporta retry sem novo evento, não projeta pré-submissão por padrão e remove estados terminais; 8 testes Persistence | `src/CryptoTrading.Persistence/OrderProjectionService.cs`, `src/CryptoTrading.Domain/Orders/OrderContracts.cs`, `tests/CryptoTrading.Persistence.Tests/OrderProjectionServiceTests.cs` | `e0dba47` + validação local | Codex |
| 2026-09-19 | Shadow soak determinístico | Três execuções do harness local, sem rede, credenciais ou capacidade de submit/cancel; relatório redigido gerado em `artifacts/shadow/` | `scripts/test-shadow-soak.sh`, `docs/shadow-soak.md` | execução local PASS | Codex |
| 2026-09-19 | Ledger recovery drill | Reconstrução determinística journal → ledger executada com 3 testes, sem rede/credenciais, e relatório redigido gerado em `artifacts/recovery/` | `scripts/test-ledger-recovery.sh`, `docs/recovery-drill.md` | execução local PASS | Codex |
| 2026-09-19 | Alert delivery retry | Retry bounded com backoff exponencial, cancelamento sem retry, propagação da falha final e integração no Worker para sinks locais; 13 Observability + 24 Worker testes | `src/CryptoTrading.Observability/ReliableAlertDelivery.cs`, `src/CryptoTrading.Worker/Program.cs` | `20fe0ca`, `278a797` + validação local | Codex |
| 2026-09-19 | Testnet probe hardening | Guardas contra endpoint fora da Spot Testnet, porta/UserInfo/query/fragment indevidos, símbolo inválido e withdrawals habilitados; 53 testes Binance | `scripts/test-binance-testnet-auth-readonly.sh`, `tools/CryptoTrading.BinanceReadOnlyProbe/Program.cs`, `src/CryptoTrading.Exchange.Binance/BinanceAuthenticatedClientOptions.cs` | `70d5abf` + validação local | Codex |
| 2026-09-19 | Reconciliation mismatch accounting | Relatórios de startup, periodicidade e reconnect contam também `OpenOrderReconciliationMismatch`, evitando evidência saudável/zerada para drift de ordens; 11 Reconciliation + 24 Worker testes | `src/CryptoTrading.Reconciliation/ReconciliationContracts.cs`, `src/CryptoTrading.Worker/*Reconciliation*HostedService.cs` | `e556f38` + validação local | Codex |
| 2026-09-19 | Reconciliation alert delivery | Startup, periodicidade e reconnect publicam alerta crítico redigido para resultado não saudável; deduplicação continua no dispatcher e o sink externo permanece fora do escopo local; 26 Worker testes | `src/CryptoTrading.Worker/ReconciliationAlertPublisher.cs`, `src/CryptoTrading.Worker/*Reconciliation*HostedService.cs`, `tests/CryptoTrading.Worker.Tests/ReconciliationAlertPublisherTests.cs` | `0be3fc8` + validação local | Codex |
| 2026-09-19 | Observabilidade | Logs JSON, correlação, métricas, `/metrics`, health, redaction e sink JSONL local opt-in; 9 testes aprovados | `src/CryptoTrading.Observability`, `docs/observability-local.md` | `8f202f8` | Codex |
| 2026-09-19 | OMS | Máquina de estados, transições inválidas, histórico e idempotência; 35 testes totais aprovados | `src/CryptoTrading.Domain/Orders` | `c48de4f` | Codex |
| 2026-09-19 | Risk | RiskPolicy versionada, limites, stale data e reason codes; 35 testes totais aprovados | `src/CryptoTrading.Domain/Risk` | `e6b0e75` | Codex |
| 2026-09-19 | Arquitetura | Fronteiras Domain e redaction de credenciais cobertas por testes | `tests/CryptoTrading.Domain.Tests/ArchitectureBoundaryTests.cs` | `822a05d` | Codex |
| 2026-09-19 | Execution pipeline | OrderIntent → RiskDecision → SubmitCommand/Outbox sem I/O; 43 testes de domínio aprovados | `src/CryptoTrading.Domain` | `ce2384f` | Codex |
| 2026-09-19 | Paper exchange | Market/Limit, fills, fees, slippage, cancelamento e idempotência determinísticos; 5 testes aprovados | `src/CryptoTrading.Exchange.Paper` | `4adf3be` | Codex |
| 2026-09-19 | Replay/backtest | Metadata, seed, timezone, fingerprint e ordenação determinística; 3 testes aprovados | `src/CryptoTrading.Backtesting` | `795379e` | Codex |
| 2026-09-19 | Persistência runtime | Store Npgsql transacional, migrations, journal/ledger/outbox, leases e retry | `src/CryptoTrading.Persistence/PostgresTradingStore.cs` | `6947d6e` | Codex |
| 2026-09-19 | Exchange rules | Filtros de preço/lote/notional, rate-limit retry e clock drift; 23 testes aprovados | `src/CryptoTrading.Exchange.Binance` | `a0dbe3c` | Codex |
| 2026-09-19 | Strategy baseline | Trend Following BTC/USDT, EMA 20/50, custos, fingerprint e no-look-ahead; 12 testes aprovados | `src/CryptoTrading.Backtesting/Strategies`, `docs/trend-following-baseline.md` | `3248863` | Codex |
| 2026-09-19 | Worker outbox | Store/migrations opt-in, dispatcher PAPER com lease/retry e `NO_TRADE` fail-closed; 3 testes aprovados | `src/CryptoTrading.Worker`, `tests/CryptoTrading.Worker.Tests` | `437cb44` | Codex |
| 2026-09-19 | Order book | Snapshot/delta, sequência, gap, duplicata, quarantine e resync; 31 testes Binance aprovados | `src/CryptoTrading.Exchange.Binance/BinanceLocalOrderBook.cs` | `9217d4f` | Codex |
| 2026-09-19 | Conta read-only | HMAC, account/balances/open orders/trades, clock drift e sem endpoints de ordem; 4 testes específicos | `src/CryptoTrading.Exchange.Binance/BinanceSpotAccountClient.cs` | `1d3f574` | Codex |
| 2026-09-19 | PostgreSQL integration | Migrations, ledger, outbox lease/retry/idempotência via Compose com senha efêmera; teste reproduzível | `scripts/test-postgres-integration.sh`, `tests/CryptoTrading.Persistence.IntegrationTests` | `f7b073a` | Codex |
| 2026-09-19 | Crash recovery | Lease expirado recuperado por novo store após reinício; owner antigo não consegue confirmar e não há segunda entrega após processamento | `tests/CryptoTrading.Persistence.IntegrationTests/PostgresTradingStoreIntegrationTests.cs`, `docs/postgres-local.md` | `4843123` | Codex |
| 2026-09-19 | Submissão ambígua | Timeout consulta `clientOrderId` antes de retry; sem duplicação no dispatcher PAPER | `src/CryptoTrading.Worker`, testes Worker | `9b89445` | Codex |
| 2026-09-19 | Validação OOS | Dataset sintético BTCUSDT v1.0.0 com SHA-256 e métricas exclusivamente OOS; 24 testes Backtesting | `src/CryptoTrading.Backtesting/OutOfSampleEvaluation.cs`, `docs/trend-following-baseline.md` | `946bc87` | Codex |
| 2026-09-19 | OOS sensitivity local | Três casos preregistrados percorrem as mesmas seis janelas cronológicas duas vezes, preservam fingerprints e não selecionam vencedor; 1 teste dedicado aprovado sem rede/credenciais | `tests/CryptoTrading.Backtesting.Tests/OosSensitivityTests.cs`, `scripts/test-oos-sensitivity.sh`, `docs/oos-sensitivity-local.md`, `artifacts/oos/sensitivity-*.md` | `cf25d94` + validação local | Codex |
| 2026-09-19 | Quality gate de dataset externo | `ClosedBarEnvelope` valida sequência, símbolo, timestamps UTC, hash/identidade e disponibilidade da barra; look-ahead e ordenação inválida falham fechado; 3 testes Backtesting | `src/CryptoTrading.Backtesting/MarketDataQualityGate.cs`, `tests/CryptoTrading.Backtesting.Tests/MarketDataQualityGateTests.cs`, `docs/market-data-recorder.md` | `b5ee287` + validação local | Codex |
| 2026-09-19 | Market risk | Slippage/volatilidade/liquidez locais, limites fail-closed e 8 testes adicionais | `src/CryptoTrading.Domain/Risk/MarketRiskCalculator.cs`, `docs/readiness/` | `05bcc57` | Codex |
| 2026-09-19 | Readiness snapshots | PRM, canary draft, reconciliação, release evidence e traceability; decisão No-Go preservada | `docs/readiness/` | `e9c6581` | Codex |
| 2026-09-19 | Readiness validator | Validador fail-closed para PRM, CanaryPlan e RiskPolicy, exigindo aprovação, validade, thresholds/unidades/janelas e hash; contrato incluído no CI | `scripts/validate-readiness.sh`, `scripts/test-readiness-validator.sh`, `docs/readiness/README.md` | `7b58772` | Codex |
| 2026-09-19 | API operational status | Endpoint somente leitura com estado process-local, revisão, razão e flag explícita de que endpoints mutáveis estão desabilitados | `src/CryptoTrading.Api/Program.cs`, `docs/operational-status-api.md` | validação local | Codex |
| 2026-09-19 | RiskPolicy registry | Documento canônico SHA-256, validade UTC, aprovação segregada, lifecycle append-only, supersession transacional e gate opcional do Worker; 5 testes PostgreSQL | `src/CryptoTrading.Domain/Risk/RiskPolicyDocument.cs`, `src/CryptoTrading.Persistence/Migrations/006_risk_policy_registry.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `src/CryptoTrading.Worker/RiskPolicyStartupHostedService.cs` | `79647a5` + validação local | Codex |
| 2026-09-19 | Account stream persistence | Migration 007, envelope JSON canonicalizado com SHA-256, dedupe idempotente e leitura por janela; 6 testes PostgreSQL | `src/CryptoTrading.Persistence/AccountStreamEvents.cs`, `src/CryptoTrading.Persistence/Migrations/007_account_stream_events.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs` | validação local | Codex |
| 2026-09-19 | Contratos de camadas | Interfaces explícitas para Strategy, Portfolio, Risk, Execution, OMS e Exchange sem dependências de adapter | `src/CryptoTrading.Domain/Architecture/TradingLayerContracts.cs` | `ead5138` | Codex |
| 2026-09-19 | Backup/restore + RPO/RTO local | Dump custom PostgreSQL restaurado em banco efêmero, marcador verificado, tempos de backup/restore/validação medidos e relatório redigido com permissão 600; recursos temporários removidos | `scripts/test-postgres-backup-restore.sh`, `docs/recovery-drill.md`, `artifacts/recovery/postgres-rpo-rto-*.md` | Validação local aprovada; RPO/RTO de produção permanece pendente | Codex |
| 2026-09-19 | Full recovery drill composto | Ledger recovery, manifesto de estado, PostgreSQL integration/restart e backup/restore executados juntos; 4 componentes PASS, duração agregada e limitações registradas sem copiar logs brutos | `scripts/test-full-recovery-drill.sh`, `docs/full-state-reconstruction.md`, `docs/recovery-drill.md`, `artifacts/recovery/full-recovery-*.md` | Validação local aprovada; reconstrução completa de produção permanece pendente | Codex |
| 2026-09-19 | Manifesto full-state local | Journal, open-order projection, Account Stream evidence e metadados do outbox validados read-only; SHA-256 canônico independente da ordem de leitura; 7 testes Persistence | `src/CryptoTrading.Persistence/FullStateReconstruction.cs`, `tests/CryptoTrading.Persistence.Tests/FullStateReconstructionTests.cs`, `docs/full-state-reconstruction.md` | `375d0a7` + validação local | Codex |
| 2026-09-19 | Reconciliação full-state local | Comparação pré/pós-restore read-only verifica hash, contagens, timestamp final do ledger e presença das fontes; divergência gera razões explícitas e falha fechado; 2 testes adicionais | `src/CryptoTrading.Persistence/FullStateReconciliation.cs`, `tests/CryptoTrading.Persistence.Tests/FullStateReconstructionTests.cs`, `docs/full-state-reconstruction.md` | `8812f68` + validação local | Codex |
| 2026-09-19 | Read-only offline preflight | Probe autenticado recusou antes da rede sem credenciais, `.env` ou capacidade de trade; `NO_TRADE=true`/`PAPER` e withdrawals false exigidos; relatório redigido gerado | `scripts/test-binance-readonly-preflight.sh`, `docs/runbook-local.md`, `artifacts/binance/read-only-preflight-*.md` | `03732bf` + validação local | Codex |
| 2026-09-19 | Webhook de alertas opt-in | Sink HTTP redigido exige HTTPS/443, allowlist explícita, sem query/userinfo/headers de autenticação, timeout e falha em não-2xx; desabilitado por padrão; 6 testes Observability fake | `src/CryptoTrading.Observability/HttpWebhookTradingAlertSink.cs`, `tests/CryptoTrading.Observability.Tests/HttpWebhookTradingAlertSinkTests.cs`, `docs/observability-local.md` | `544a156`, `59c79c2` + validação local | Codex |
| 2026-09-19 | Integração Worker de webhook | URL vazia mantém sink local; URL preenchida exige allowlist/timeout e validação fail-closed no startup; Worker não altera `PAPER/NO_TRADE`; 1 teste Worker adicional | `src/CryptoTrading.Worker/Program.cs`, `src/CryptoTrading.Worker/WorkerRuntimeOptions.cs`, `.env.example` | `ea008ec` + validação local | Codex |
| 2026-09-19 | Evidência pós-reconciliação redigida | Contrato fail-closed rejeita timestamp não-UTC, modo mutável, contagens inconsistentes e estado saudável com drift antes de persistir; relatório não carrega balances, segredos ou payloads | `src/CryptoTrading.Worker/ReconciliationReportWriter.cs`, `tests/CryptoTrading.Worker.Tests/ReconciliationReportWriterTests.cs` | `3c3a3f6` + 30 testes Worker | Codex |
| 2026-09-19 | Shadow operational quality gate | Thresholds explícitos para observações, divergência, stale, recovery, intervention-rate e duração; runner recusa credenciais e gera evidência redigida local | `src/CryptoTrading.Shadow/ShadowOperationalGate.cs`, `scripts/test-shadow-operational-gate.sh`, `docs/shadow-operational-gate.md`, `tests/CryptoTrading.Shadow.Tests/ShadowOperationalGateTests.cs` | `04ac8c9` + 7 testes Shadow + runner PASS | Codex |
| 2026-09-19 | Readiness expiry gate | Validator rejeita datas inválidas ou expiradas no PRM/RiskPolicy, aceita `READINESS_AS_OF_UTC` para auditorias determinísticas e mantém aprovação humana obrigatória | `scripts/validate-readiness.sh`, `scripts/test-readiness-validator.sh`, `docs/readiness/README.md` | `214d462` + contrato do validator PASS | Codex |
| 2026-09-19 | Canary expiry gate | Validator também exige e valida a validade UTC do CanaryPlan, rejeitando datas inválidas ou expiradas antes de qualquer promoção | `scripts/validate-readiness.sh`, `scripts/test-readiness-validator.sh`, `docs/readiness/README.md` | `d170a3e` + contrato do validator PASS | Codex |
| 2026-09-19 | OMS fill idempotency | Aggregate `Order` aplica fills parciais exatamente uma vez, rejeita duplicata conflitante/overfill, preserva fills após cancelamento e não retrocede `UpdatedAtUtc` em eventos fora de ordem | `src/CryptoTrading.Domain/Orders/OrderContracts.cs`, `tests/CryptoTrading.Domain.Tests/OrderTests.cs`, `docs/oms-fill-idempotency.md` | `7589429` + 78 testes Domain | Codex |
| 2026-09-19 | Release evidence local | Gerador canônico redigido valida janela, decisão, responsáveis, fontes, métricas e follow-ups; ordenação determinística e SHA-256; metadados com aparência de segredo são rejeitados | `src/CryptoTrading.Shadow/PostReleaseReport.cs`, `tests/CryptoTrading.Shadow.Tests/PostReleaseReportTests.cs`, `docs/release-report-local.md` | validação local + 10 testes Shadow | Codex |
| 2026-09-19 | Bundle de release evidence e rastreabilidade | Escritor imutável produz relatório JSON + manifesto sidecar com SHA-256, referências ordenadas, `promotion_authorized=false`, permissões `0600`, idempotência e colisão fail-closed; 2 testes dedicados | `src/CryptoTrading.Shadow/PostReleaseEvidenceWriter.cs`, `tests/CryptoTrading.Shadow.Tests/PostReleaseReportTests.cs`, `docs/release-report-local.md` | validação local + 12 testes Shadow | Codex |
| 2026-09-19 | Operational-control audit persistence | Kill switch/SafeMode emite audit event versionado com hash, usa sink append-only in-memory no modo local e não altera estado se a persistência do audit falhar | `src/CryptoTrading.Domain/Risk/OperationalControl.cs`, `tests/CryptoTrading.Domain.Tests/OperationalControlTests.cs` | validação local + 80 testes Domain | Codex |
| 2026-09-19 | Operational-control audit PostgreSQL | Migration 010 append-only com trigger contra UPDATE/DELETE, persistência assíncrona idempotente por evento/chave, leitura com verificação SHA-256 e colisão fail-closed; 8 testes PostgreSQL | `src/CryptoTrading.Persistence/Migrations/010_operational_control_audit.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `tests/CryptoTrading.Persistence.IntegrationTests/PostgresTradingStoreIntegrationTests.cs`, `docs/persistence-migrations.md` | `d32f5ac` + `./scripts/test-postgres-integration.sh` PASS | Codex |
| 2026-09-19 | Worker audit sink wiring | Registro único de `IOperationalControl`; padrão in-memory e seleção PostgreSQL somente com `WORKER_PERSISTENCE_ENABLED=true`; falha de persistência mantém transição fail-closed; 2 testes DI | `src/CryptoTrading.Worker/OperationalControlRegistration.cs`, `src/CryptoTrading.Worker/Program.cs`, `tests/CryptoTrading.Worker.Tests/WorkerRuntimeTests.cs`, `docs/persistence-migrations.md` | `677f15b` + 32 testes Worker | Codex |
| 2026-09-19 | Worker persistence startup smoke | Host real inicializado com PostgreSQL efêmero, migration 010 aplicada e sink de auditoria selecionado sob `WORKER_PERSISTENCE_ENABLED=true`; PAPER/NO_TRADE, sem Binance/reconciliação/account stream | `scripts/test-worker-persistence-startup.sh`, `docs/runbook-local.md` | validação local PASS | Codex |
| 2026-09-19 | Experiment governance foundation (US-054) | Preregistration canônica com hash/idempotência, budget de trials, benchmark/rejection rule, auditoria de holdout, contaminação irreversível e registro obrigatório da versão de replacement; 8 testes Domain | `src/CryptoTrading.Domain/Research/ExperimentRegistry.cs`, `tests/CryptoTrading.Domain.Tests/ExperimentRegistryTests.cs`, `docs/experiment-governance.md` | validação local + 88 testes Domain | Codex |
| 2026-09-19 | Experiment governance PostgreSQL | Migration 011 e `IExperimentGovernanceStore` persistem preregistration, trials, holdout access e contamination events com hashes, idempotência, constraints e append-only; integração PostgreSQL 9/9 verde | `src/CryptoTrading.Persistence/Migrations/011_experiment_governance.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `tests/CryptoTrading.Persistence.IntegrationTests/PostgresTradingStoreIntegrationTests.cs`, `docs/persistence-migrations.md` | `./scripts/test-postgres-integration.sh` PASS | Codex |
| 2026-09-19 | Experiment governance restart reconstruction | Leitura transacional recompõe preregistration, trials, hash, acessos e contaminação após nova instância do store; hashes inválidos falham fechado | `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `tests/CryptoTrading.Persistence.IntegrationTests/PostgresTradingStoreIntegrationTests.cs` | integração PostgreSQL 9/9 PASS | Codex |
| 2026-09-19 | Experiment evidence bundle | Artefato JSON + manifesto canônico redigido, SHA-256, referências ordenadas, permissões 0600, idempotência e bloqueio de promoção para holdout contaminado; 3 testes Backtesting | `src/CryptoTrading.Backtesting/ExperimentEvidenceWriter.cs`, `tests/CryptoTrading.Backtesting.Tests/ExperimentEvidenceWriterTests.cs`, `docs/experiment-governance.md` | validação local + 46 testes Backtesting | Codex |
| 2026-09-19 | Benchmark comparison gate | Comparação determinística da estratégia contra `NO_TRADE/cash` e `buy-and-hold`, com drawdown, custos declarados e fingerprint; writer exige os dois benchmarks | `src/CryptoTrading.Backtesting/BenchmarkComparison.cs`, `src/CryptoTrading.Backtesting/ExperimentEvidenceWriter.cs`, `tests/CryptoTrading.Backtesting.Tests/BenchmarkComparisonTests.cs` | validação local + 46 testes Backtesting | Codex |
| 2026-09-19 | Permissões da evidência local | Sinks JSONL de alertas, Shadow e reconciliação aplicam modo Unix `0600` e testes verificam que somente o usuário proprietário lê/escreve os arquivos | `src/CryptoTrading.Observability/TradingAlerts.cs`, `src/CryptoTrading.Shadow/ShadowReportWriter.cs`, `src/CryptoTrading.Worker/ReconciliationReportWriter.cs`, `docs/observability-local.md` | `0e06648` + 19/7/30 testes relacionados | Codex |
| 2026-09-19 | Property tests | Gerador xorshift determinístico cobrindo balanceamento, idempotência, atomicidade e saldos | `tests/CryptoTrading.Domain.Tests/AccountingPropertyTests.cs` | `5f7d465` | Codex |
| 2026-09-19 | Simulador Paper/Replay | Spread, latência, partial fills, rejeições e limites determinísticos; 8 testes Paper | `src/CryptoTrading.Exchange.Paper`, `docs/paper-replay-simulator.md` | `955fe1b` | Codex |
| 2026-09-19 | Monte Carlo/stress | Bootstrap determinístico por seed, shocks adversos, quantis P05/P50/P95, drawdown e probabilidade de ruína; 29 testes Backtesting | `src/CryptoTrading.Backtesting/MonteCarloStress.cs` | `8d27838` | Codex |
| 2026-09-19 | Paper chaos/soak | 64 ordens, duplicação de produtor, falhas transitórias, crash pós-aceite e leases recuperados sem side effects duplicados | `tests/CryptoTrading.Worker.Tests/PaperSoakChaosTests.cs`, `scripts/test-paper-soak-chaos.sh` | `fba5f92` | Codex |
| 2026-09-19 | Probe autenticado | Harness Testnet read-only fail-closed consulta account/balances, open orders e até 100 trades recentes; sem endpoints de mutação e saída redigida; ausência de credenciais validada, execução real aguarda chaves novas | `tools/CryptoTrading.BinanceReadOnlyProbe`, `scripts/test-binance-testnet-auth-readonly.sh` | `674747a`, `cf89005`, `69f0351` + validação de build/guard | Codex |
| 2026-09-19 | Probe Testnet hardening | Endpoint autenticado exige HTTPS/host/path/porta 443 sem userinfo/query, símbolo é normalizado e validado, script rejeita URL/permissão de withdrawals inválidas e grava relatório sem stdout/stderr; ausência de credenciais retorna exit 2 | `tools/CryptoTrading.BinanceReadOnlyProbe`, `scripts/test-binance-testnet-auth-readonly.sh`, `tests/CryptoTrading.Exchange.Binance.Tests/BinanceSpotAccountClientTests.cs` | testes Binance: 53; guards de ausência/endpoint/withdrawals: exit 2; build probe sem warnings | `70d5abf` |
| 2026-09-19 | Revisão Risk/Execution | Endpoint arbitrário, timestamp futuro e poison message identificados e corrigidos; relatório independente preservado | `docs/reviews/2026-09-19-risk-execution-review.md`, `src/CryptoTrading.Persistence/Migrations/004_outbox_dead_letter.up.sql` | `887b8d1`, `77484a6` | Codex |
| 2026-09-19 | Outbox poison messages | Limite configurável de 1–100 tentativas, dead-letter persistente e migration 004; 7 testes Worker | `src/CryptoTrading.Worker`, `src/CryptoTrading.Persistence/Migrations/004_outbox_dead_letter.up.sql` | `c2478ef`, `77484a6` | Codex |
| 2026-09-19 | DTOs autenticados | Validação fail-closed de account, balances, open orders e trades antes de reconciliação; 40 testes Binance | `src/CryptoTrading.Exchange.Binance/BinanceAuthenticatedResponseValidator.cs` | `ecd4b93`, `b7559b9` | Codex |
| 2026-09-19 | Retry read-only | Cliente autenticado usa backoff limitado e `Retry-After` para 429/408/5xx, sem expor corpo ou assinatura | `src/CryptoTrading.Exchange.Binance/BinanceSpotAccountClient.cs` | `c5fc668` | Codex |
| 2026-09-19 | Alert delivery local | Envelope operacional UTC com owner/ação/runbook, deduplicação, retry após falha e métricas; sink em memória cobre somente drills locais | `src/CryptoTrading.Observability/TradingAlerts.cs`, `tests/CryptoTrading.Observability.Tests/TradingAlertDispatcherTests.cs` | `bdc9acd` | Codex |
| 2026-09-19 | Reconstrução de ledger | `LedgerReconstructor` recompõe a projeção a partir do journal em ordem determinística; runner dedicado produz relatório redigido com 3 testes aprovados; reconstrução completa de estado e RPO/RTO de produção permanecem pendentes | `src/CryptoTrading.Domain/Accounting/LedgerReconstruction.cs`, `tests/CryptoTrading.Domain.Tests/LedgerReconstructionTests.cs`, `scripts/test-ledger-recovery.sh`, `docs/recovery-drill.md` | `db1521f` + validação local | Codex |
| 2026-09-19 | Shadow drift evaluator/runner | Avaliador e runner conectam decisões da estratégia a observações redigidas por sequência, timestamp e fingerprint; não enviam ordens e não substituem soak Shadow real | `src/CryptoTrading.Backtesting/ShadowDrift.cs`, `tests/CryptoTrading.Backtesting.Tests/ShadowDriftTests.cs` | `4b09eed`, `5cb5a86` | Codex |
| 2026-09-19 | Shadow soak local | 64 observações sintéticas, divergência/stale/recovery controlados; runner repete 3 janelas por padrão, agrega resumos e falha fechado em qualquer erro ou credencial presente | `scripts/test-shadow-soak.sh`, `tests/CryptoTrading.Backtesting.Tests/ShadowSoakTests.cs`, `docs/shadow-soak.md` | `4b9966c`, `9192644` + validação 3x | Codex |
| 2026-09-19 | Paper Shadow adapter | `PaperShadowSession` executa decisões da estratégia no `PaperExchange`, aplica ticks e converte fills em observações Shadow sem rede ou credenciais | `src/CryptoTrading.Shadow/PaperShadowSession.cs`, `tests/CryptoTrading.Shadow.Tests/PaperShadowSessionTests.cs` | `51b5a2d` | Codex |
| 2026-09-19 | Worker Shadow gate | `WORKER_SHADOW_ENABLED` é opt-in, exige `WORKER_NO_TRADE=true` e executa somente um drill local no startup; fixture alinhado ao relógio evita falso stale e runtime confirmou zero divergências | `src/CryptoTrading.Worker/ShadowDrillHostedService.cs`, `tests/CryptoTrading.Worker.Tests/WorkerRuntimeTests.cs`, `tests/CryptoTrading.Shadow.Tests/PaperShadowSessionTests.cs` | `f494524`, `3e1982b` + validação runtime | Codex |
| 2026-09-19 | Shadow report/alerts | Worker acrescenta relatório JSONL redigido e publica alertas locais deduplicados para divergência, stale e recovery; runtime e testes de serviço confirmaram persistência/guardas, canais externos permanecem pendentes | `src/CryptoTrading.Shadow/ShadowReportWriter.cs`, `src/CryptoTrading.Worker/ShadowAlertPublisher.cs`, `src/CryptoTrading.Worker/ShadowDrillHostedService.cs`, `tests/CryptoTrading.Shadow.Tests/PaperShadowSessionTests.cs`, `tests/CryptoTrading.Worker.Tests/ShadowDrillHostedServiceTests.cs` | `79c70dc`, `e4aea1f`, `2da5188` + validação runtime | Codex |
| 2026-09-19 | Risk operations | SafeMode/KillSwitch idempotente, reset autorizado e bloqueio de novas entradas; drill local dedicado com 7 testes e relatório redigido | `src/CryptoTrading.Domain/Execution`, `tests/CryptoTrading.Domain.Tests/OperationalControlTests.cs`, `scripts/test-operational-control.sh`, `docs/runbook-local.md` | `e080792` + validação local | Codex |
| 2026-09-19 | Backtest metrics | Equity, retorno líquido, fees, slippage, turnover, drawdown, Sharpe parametrizado e planos OOS | `src/CryptoTrading.Backtesting/TrendFollowingBacktest.cs` | `c06b038` | Codex |
| 2026-09-19 | Market-data recorder | JSONL append-only por dia UTC, envelope v1, checksum SHA-256, retenção explícita e replay determinístico; 22 testes Backtesting | `src/CryptoTrading.Backtesting/MarketDataRecorder.cs`, `docs/market-data-recorder.md` | `83657bd` | Codex |
| 2026-09-19 | Reconciliação | Comparação da união de ativos do ledger e da conta, SafeMode em drift, snapshot stale/futuro e paginação deduplicada de trades; 9 testes | `src/CryptoTrading.Reconciliation`, `tests/CryptoTrading.Reconciliation.Tests/AccountReconciliationServiceTests.cs`, `docs/readiness/RECON-20260919-local.md` | `3332113` + validação local | Codex |
| 2026-09-19 | Worker reconciliation gate | Startup read-only opt-in exige PostgreSQL e `NO_TRADE`; adapter de saldo e known-trade IDs PostgreSQL, SafeMode, logging redigido e relatório JSONL append-only; default não registra cliente autenticado; ausência de credenciais falha antes da rede | `src/CryptoTrading.Worker/ReconciliationStartupHostedService.cs`, `src/CryptoTrading.Worker/ReconciliationReportWriter.cs`, `src/CryptoTrading.Worker/Program.cs`, `src/CryptoTrading.Worker/WorkerRuntimeOptions.cs`, `.env.example`, `docs/runbook-local.md` | `e10f5de`, `a8aa21f`, `ceaef88` + validação local | Codex |
| 2026-09-19 | Strategy Health | Healthy/NoTrade/Blocked, freshness, risco, reconciliação e kill switch | `src/CryptoTrading.Domain/Risk/StrategyHealth.cs` | `8ffb4d8` | Codex |
| 2026-09-19 | Operação local | Runbook, threat model, backup/restore/RPO-RTO local, Paper chaos/soak, Shadow soak repetível e alertas JSONL locais executados sem capital; dados reais, canais externos e RPO/RTO de produção continuam pendentes | `docs/runbook-local.md`, `docs/threat-model-local.md`, `docs/recovery-drill.md`, `docs/shadow-soak.md`, `docs/readiness/AUDIT-20260919-current.md` | `8f202f8` + validação local | Codex |

## Bloqueios e decisões

| Data | Bloqueio/decisão | Impacto | Próxima ação | Estado |
|---|---|---|---|---|
| 2026-09-19 | Credenciais anteriormente expostas não podem ser usadas | Bloqueia integração privada | Revogar e gerar nova chave Testnet | Aberto |
