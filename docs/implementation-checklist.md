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
- [x] Validar restart, crash recovery de leases, idempotência durável e reconstrução determinística local; RPO/RTO físico completo permanece pendente.

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
- [x] Executar validação OOS com dataset sintético versionado e comparar janelas walk-forward; sensibilidade de execução permanece pendente.
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
| 2026-09-19 | Market risk | Slippage/volatilidade/liquidez locais, limites fail-closed e 8 testes adicionais | `src/CryptoTrading.Domain/Risk/MarketRiskCalculator.cs`, `docs/readiness/` | `05bcc57` | Codex |
| 2026-09-19 | Readiness snapshots | PRM, canary draft, reconciliação, release evidence e traceability; decisão No-Go preservada | `docs/readiness/` | `e9c6581` | Codex |
| 2026-09-19 | Readiness validator | Validador fail-closed para PRM, CanaryPlan e RiskPolicy, exigindo aprovação, validade, thresholds/unidades/janelas e hash; contrato incluído no CI | `scripts/validate-readiness.sh`, `scripts/test-readiness-validator.sh`, `docs/readiness/README.md` | `7b58772` | Codex |
| 2026-09-19 | API operational status | Endpoint somente leitura com estado process-local, revisão, razão e flag explícita de que endpoints mutáveis estão desabilitados | `src/CryptoTrading.Api/Program.cs`, `docs/operational-status-api.md` | validação local | Codex |
| 2026-09-19 | RiskPolicy registry | Documento canônico SHA-256, validade UTC, aprovação segregada, lifecycle append-only, supersession transacional e gate opcional do Worker; 5 testes PostgreSQL | `src/CryptoTrading.Domain/Risk/RiskPolicyDocument.cs`, `src/CryptoTrading.Persistence/Migrations/006_risk_policy_registry.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs`, `src/CryptoTrading.Worker/RiskPolicyStartupHostedService.cs` | `79647a5` + validação local | Codex |
| 2026-09-19 | Account stream persistence | Migration 007, envelope JSON canonicalizado com SHA-256, dedupe idempotente e leitura por janela; 6 testes PostgreSQL | `src/CryptoTrading.Persistence/AccountStreamEvents.cs`, `src/CryptoTrading.Persistence/Migrations/007_account_stream_events.up.sql`, `src/CryptoTrading.Persistence/PostgresTradingStore.cs` | validação local | Codex |
| 2026-09-19 | Contratos de camadas | Interfaces explícitas para Strategy, Portfolio, Risk, Execution, OMS e Exchange sem dependências de adapter | `src/CryptoTrading.Domain/Architecture/TradingLayerContracts.cs` | `ead5138` | Codex |
| 2026-09-19 | Backup/restore | Dump custom PostgreSQL restaurado em banco efêmero e verificado; recursos temporários removidos | `scripts/test-postgres-backup-restore.sh`, `docs/recovery-drill.md` | Validação local aprovada | Codex |
| 2026-09-19 | Property tests | Gerador xorshift determinístico cobrindo balanceamento, idempotência, atomicidade e saldos | `tests/CryptoTrading.Domain.Tests/AccountingPropertyTests.cs` | `5f7d465` | Codex |
| 2026-09-19 | Simulador Paper/Replay | Spread, latência, partial fills, rejeições e limites determinísticos; 8 testes Paper | `src/CryptoTrading.Exchange.Paper`, `docs/paper-replay-simulator.md` | `955fe1b` | Codex |
| 2026-09-19 | Monte Carlo/stress | Bootstrap determinístico por seed, shocks adversos, quantis P05/P50/P95, drawdown e probabilidade de ruína; 29 testes Backtesting | `src/CryptoTrading.Backtesting/MonteCarloStress.cs` | `8d27838` | Codex |
| 2026-09-19 | Paper chaos/soak | 64 ordens, duplicação de produtor, falhas transitórias, crash pós-aceite e leases recuperados sem side effects duplicados | `tests/CryptoTrading.Worker.Tests/PaperSoakChaosTests.cs`, `scripts/test-paper-soak-chaos.sh` | `fba5f92` | Codex |
| 2026-09-19 | Probe autenticado | Harness Testnet read-only fail-closed consulta account/balances, open orders e até 100 trades recentes; sem endpoints de mutação e saída redigida; ausência de credenciais validada, execução real aguarda chaves novas | `tools/CryptoTrading.BinanceReadOnlyProbe`, `scripts/test-binance-testnet-auth-readonly.sh` | `674747a`, `cf89005`, `69f0351` + validação de build/guard | Codex |
| 2026-09-19 | Revisão Risk/Execution | Endpoint arbitrário, timestamp futuro e poison message identificados e corrigidos; relatório independente preservado | `docs/reviews/2026-09-19-risk-execution-review.md`, `src/CryptoTrading.Persistence/Migrations/004_outbox_dead_letter.up.sql` | `887b8d1`, `77484a6` | Codex |
| 2026-09-19 | Outbox poison messages | Limite configurável de 1–100 tentativas, dead-letter persistente e migration 004; 7 testes Worker | `src/CryptoTrading.Worker`, `src/CryptoTrading.Persistence/Migrations/004_outbox_dead_letter.up.sql` | `c2478ef`, `77484a6` | Codex |
| 2026-09-19 | DTOs autenticados | Validação fail-closed de account, balances, open orders e trades antes de reconciliação; 40 testes Binance | `src/CryptoTrading.Exchange.Binance/BinanceAuthenticatedResponseValidator.cs` | `ecd4b93`, `b7559b9` | Codex |
| 2026-09-19 | Retry read-only | Cliente autenticado usa backoff limitado e `Retry-After` para 429/408/5xx, sem expor corpo ou assinatura | `src/CryptoTrading.Exchange.Binance/BinanceSpotAccountClient.cs` | `c5fc668` | Codex |
| 2026-09-19 | Alert delivery local | Envelope operacional UTC com owner/ação/runbook, deduplicação, retry após falha e métricas; sink em memória cobre somente drills locais | `src/CryptoTrading.Observability/TradingAlerts.cs`, `tests/CryptoTrading.Observability.Tests/TradingAlertDispatcherTests.cs` | `bdc9acd` | Codex |
| 2026-09-19 | Reconstrução de ledger | `LedgerReconstructor` recompõe a projeção a partir do journal em ordem determinística; runner dedicado produz relatório redigido com 3 testes aprovados; RPO/RTO PostgreSQL completo permanece pendente | `src/CryptoTrading.Domain/Accounting/LedgerReconstruction.cs`, `tests/CryptoTrading.Domain.Tests/LedgerReconstructionTests.cs`, `scripts/test-ledger-recovery.sh`, `docs/recovery-drill.md` | `db1521f` + validação local | Codex |
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
| 2026-09-19 | Operação local | Runbook, threat model, backup/restore isolado, Paper chaos/soak, Shadow soak repetível e alertas JSONL locais executados sem capital; dados reais, canais externos e RPO/RTO continuam pendentes | `docs/runbook-local.md`, `docs/threat-model-local.md`, `docs/recovery-drill.md`, `docs/shadow-soak.md`, `docs/readiness/AUDIT-20260919-current.md` | `8f202f8` + validação local | Codex |

## Bloqueios e decisões

| Data | Bloqueio/decisão | Impacto | Próxima ação | Estado |
|---|---|---|---|---|
| 2026-09-19 | Credenciais anteriormente expostas não podem ser usadas | Bloqueia integração privada | Revogar e gerar nova chave Testnet | Aberto |
