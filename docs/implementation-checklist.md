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

## Fase 1 — Domínio e contratos

- [x] Adicionar `TimeProvider` injetável e relógio determinístico.
- [x] Criar IDs de domínio, correlation ID e idempotency key.
- [x] Criar tipos de dinheiro, preço, quantidade e percentual com `decimal`.
- [x] Modelar estados de ordem, fill e transições explícitas do ciclo de vida.
- [ ] Definir contratos Strategy → Portfolio → Risk → Execution → OMS → Exchange.
- [x] Criar testes unitários básicos para primitives e relógio.
- [x] Adicionar testes de arquitetura para fronteiras do domínio.
- [ ] Adicionar property-based tests financeiros.

## Fase 2 — Persistência financeira local

- [x] Adicionar PostgreSQL 18 ao Docker Compose, com volume nomeado, bind local e healthcheck.
- [x] Criar migrations SQL versionadas, rollback explícito e política de backup local.
- [x] Implementar journal imutável e ledger de saldos, fees, posições e PnL (contrato in-memory e store PostgreSQL transacional).
- [x] Implementar outbox/inbox com lease, retry e deduplicação.
- [x] Registrar o store no Worker sob flag explícita e aplicar migrations sob flag explícita.
- [x] Implementar dispatcher PAPER com lease, retry, logs sem payload e default `NO_TRADE`.
- [x] Implementar readiness real para banco e dependências.
- [ ] Validar restart, crash recovery e reconstrução de estado.

## Fase 3 — Binance read-only e market data

- [x] Implementar adapter REST read-only (`ping`, `time`, `exchangeInfo`).
- [x] Validar ambiente, endpoint e permissões fail-closed.
- [x] Implementar rate limits, retry, backoff e clock drift.
- [x] Implementar `exchangeInfo`, filtros e precisão por símbolo.
- [x] Implementar trades/candles via WebSocket e reconexão pública resiliente.
- [x] Implementar order book local, gap detection e resync.
- [ ] Implementar recorder/replay de dados selecionados.

## Fase 4 — OMS, reconciliação e risco

- [x] Implementar máquina de estados de ordens e `clientOrderId` idempotente.
- [ ] Garantir persist-before-submit e tratamento de timeout ambíguo.
- [ ] Implementar reconciliação de balances, ordens, fills e fees.
- [x] Implementar `RiskPolicy` versionada.
- [x] Implementar limites de exposição, notional, daily loss e drawdown.
- [x] Implementar controles de stale data e bloqueio de withdrawals.
- [ ] Implementar slippage, volatilidade e liquidez com dados de mercado.
- [x] Implementar kill switch, SafeMode, reset autorizado e testes de falha.

## Fase 5 — Integração autenticada Testnet

- [ ] Revogar chaves expostas e gerar nova chave Spot Testnet.
- [ ] Confirmar withdrawals, transferências, margem e Futures desativados.
- [ ] Preencher credenciais somente no `.env` local ou secret store externo.
- [x] Implementar adapter autenticado read-only para account, balances, open orders e trades (sem submit/cancel).
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
- [ ] Executar validação OOS com dataset versionado e comparar casos de sensibilidade.
- [ ] Evoluir o simulador para spread, latência, partial fills, rejeições, limites e impacto de mercado.
- [ ] Implementar Monte Carlo e stress tests depois de validar o modelo de execução.

## Fase 7 — Shadow, observabilidade e operação

- [ ] Implementar Strategy Health e estado `NO_TRADE`.
- [x] Instrumentar logs JSON, correlação, métricas e `ActivitySource` local.
- [ ] Criar alertas, redaction, dashboards e runbooks.
- [ ] Criar threat model.
- [ ] Executar backup/restore, chaos e soak test em shadow.

## Fase 8 — Readiness e canary

- [ ] Aprovar `RiskPolicy`.
- [ ] Preencher `PromotionReadinessMatrix` e `CanaryPlan`.
- [ ] Preencher `ThreatModel`, `ReconciliationReport` e `ReleaseEvidence`.
- [ ] Vincular critérios de aceitação ao manifesto de rastreabilidade.
- [ ] Realizar revisão independente de Risk/Execution.
- [ ] Aprovar Go/No-Go e executar canary sem capital.

## Registro de execução

| Data | Item | Resultado | Evidência | Commit | Responsável |
|---|---|---|---|---|---|
| 2026-09-19 | Bootstrap | Build .NET 11: 0 warnings/0 errors | `dotnet build --no-restore` | `7be8714` | Codex |
| 2026-09-19 | Binance pública | `ping`, `time`, `exchangeInfo` aprovados | `scripts/test-binance-testnet-public.sh` | `828a449` | Codex |
| 2026-09-19 | CI/gates | Restore, build Release, 9 testes, format e secret scan aprovados; Compose requer `POSTGRES_PASSWORD` explícita | `.github/workflows/quality.yml`, `scripts/scan-secrets.sh` | `64b333c` | Codex |
| 2026-09-19 | Domínio | Primitives e relógio determinístico; build sem warnings e 9 testes aprovados | `src/CryptoTrading.Domain/Primitives`, `tests/CryptoTrading.Domain.Tests` | `319b4fc` | Codex |
| 2026-09-19 | PostgreSQL local | Compose validado com senha temporária, bind em loopback e healthcheck | `compose.yaml`, `docs/postgres-local.md` | `66e982d` | Codex |
| 2026-09-19 | Contabilidade | Journal/ledger in-memory com idempotência, balanceamento e atomicidade; 15 testes aprovados | `src/CryptoTrading.Domain/Accounting`, testes de domínio | `50cba3b` | Codex |
| 2026-09-19 | Configuração | `.env` carregado localmente e validação Testnet fail-closed; 5 testes aprovados | `src/CryptoTrading.Configuration` | `9a11432` | Codex |
| 2026-09-19 | Binance read-only | Cliente REST público com testes fake; 7 testes aprovados | `src/CryptoTrading.Exchange.Binance` | `9f5243f` | Codex |
| 2026-09-19 | Persistência SQL | Migrations journal/ledger/outbox, guards append-only e rollback documentado; 2 testes aprovados | `src/CryptoTrading.Persistence`, `docs/persistence-migrations.md` | `fa97f71` | Codex |
| 2026-09-19 | Market data | Streams públicos trade/kline com parsing, fragmentação, reconnect/backoff; 10 testes aprovados | `src/CryptoTrading.Exchange.Binance`, `docs/market-data-websocket.md` | `49a3abc` | Codex |
| 2026-09-19 | Observabilidade | Logs JSON, correlação, métricas, `/metrics`, health e redaction; 5 testes aprovados | `src/CryptoTrading.Observability`, `docs/observability-local.md` | `8948360` | Codex |
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
| 2026-09-19 | Risk operations | SafeMode/KillSwitch idempotente, reset autorizado e bloqueio de novas entradas | `src/CryptoTrading.Domain/Execution`, testes de domínio | `e080792` | Codex |
| 2026-09-19 | Backtest metrics | Equity, retorno líquido, fees, slippage, turnover, drawdown, Sharpe parametrizado e planos OOS | `src/CryptoTrading.Backtesting/TrendFollowingBacktest.cs` | `c06b038` | Codex |

## Bloqueios e decisões

| Data | Bloqueio/decisão | Impacto | Próxima ação | Estado |
|---|---|---|---|---|
| 2026-09-19 | Credenciais anteriormente expostas não podem ser usadas | Bloqueia integração privada | Revogar e gerar nova chave Testnet | Aberto |
