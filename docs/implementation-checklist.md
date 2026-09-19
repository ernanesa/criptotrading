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
- [ ] Modelar estados de ordem, fill, posição e decisão de risco.
- [ ] Definir contratos Strategy → Portfolio → Risk → Execution → OMS → Exchange.
- [x] Criar testes unitários básicos para primitives e relógio.
- [ ] Adicionar property-based e testes de arquitetura.

## Fase 2 — Persistência financeira local

- [x] Adicionar PostgreSQL 18 ao Docker Compose, com volume nomeado, bind local e healthcheck.
- [ ] Criar migrations e política de backup local.
- [ ] Implementar journal imutável e ledger de saldos, fees, posições e PnL.
- [ ] Implementar outbox/inbox com lease, retry e deduplicação.
- [ ] Implementar readiness real para banco e dependências.
- [ ] Validar restart, crash recovery e reconstrução de estado.

## Fase 3 — Binance read-only e market data

- [ ] Implementar adapter REST read-only.
- [ ] Validar ambiente, endpoint e permissões fail-closed.
- [ ] Implementar rate limits, retry, backoff e clock drift.
- [ ] Implementar `exchangeInfo`, filtros e precisão por símbolo.
- [ ] Implementar trades/candles via WebSocket e reconexão.
- [ ] Implementar order book local, gap detection e resync.
- [ ] Implementar recorder/replay de dados selecionados.

## Fase 4 — OMS, reconciliação e risco

- [ ] Implementar máquina de estados de ordens e `clientOrderId` idempotente.
- [ ] Garantir persist-before-submit e tratamento de timeout ambíguo.
- [ ] Implementar reconciliação de balances, ordens, fills e fees.
- [ ] Implementar `RiskPolicy` versionada.
- [ ] Implementar limites de exposição, notional, daily loss e drawdown.
- [ ] Implementar controles de stale data, slippage, volatilidade e liquidez.
- [ ] Implementar kill switch e testes de falha.

## Fase 5 — Integração autenticada Testnet

- [ ] Revogar chaves expostas e gerar nova chave Spot Testnet.
- [ ] Confirmar withdrawals, transferências, margem e Futures desativados.
- [ ] Preencher credenciais somente no `.env` local ou secret store externo.
- [ ] Implementar account, balances, orders e fills autenticados.
- [ ] Executar primeiro teste de ordem com notional mínimo e Risk aprovado.
- [ ] Registrar evidência redacted e confirmar reconciliação após restart.

## Fase 6 — Paper, replay e estratégia baseline

- [ ] Implementar paper exchange determinística.
- [ ] Versionar dataset, configuração, seed e timezone do replay.
- [ ] Implementar backtest reproduzível.
- [ ] Especificar baseline Trend Following BTC/USDT.
- [ ] Cobrir custos, spread, slippage, fees e latência.
- [ ] Executar validação out-of-sample, walk-forward, sensibilidade e Monte Carlo.

## Fase 7 — Shadow, observabilidade e operação

- [ ] Implementar Strategy Health e estado `NO_TRADE`.
- [ ] Instrumentar logs, métricas e traces OpenTelemetry.
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

## Bloqueios e decisões

| Data | Bloqueio/decisão | Impacto | Próxima ação | Estado |
|---|---|---|---|---|
| 2026-09-19 | Credenciais anteriormente expostas não podem ser usadas | Bloqueia integração privada | Revogar e gerar nova chave Testnet | Aberto |
