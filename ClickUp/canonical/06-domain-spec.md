# 06 - Domínio e eventos

## Value Objects principais

- `Symbol`
- `Asset`
- `Price`
- `Quantity`
- `Money`
- `Percentage`
- `OrderId` / `ClientOrderId`
- `StrategyId` / `StrategyVersion`
- `ExperimentId`
- `CorrelationId`
- `Timeframe`
- `RiskAmount`
- `LedgerEntryId`
- `Balance` / `AvailableBalance` / `LockedBalance`
- `Fee` / `ValuationPrice`

## Entidades/agregados

### Order
State machine controlada pelo OMS. Nunca inferir `Filled` apenas porque uma requisição retornou sucesso.

### Position
Projeção interna de exposição, cost basis e PnL resultante do ledger, fills, fees e valuation. Binance Spot fornece balances/trades/ordens, não uma posição canônica.

### AccountLedger
Ledger multi-commodity de dupla entrada, append-only, para principal, proceeds, fees/rebates, deposits, withdrawals, conversions, adjustments e compensating entries. Cada commodity fecha contra conta/counterparty correspondente; valuation é projeção separada. Nunca corrigir histórico financeiro por update destrutivo.

### BalanceSnapshot
Snapshot externo `free/locked/total` por ativo, com source time e ingestion time, usado para reconciliação; não substitui o ledger.

### StrategyInstance
Strategy + versão + parâmetros + ambiente + status.

### RiskPolicy
Conjunto versionado de limites.

### Experiment
Dataset + strategy version + parâmetros + custos + seed + resultados.

## Sinais

`AlphaSignal` deve poder expressar:
- symbol;
- direction;
- strength/confidence;
- expected return opcional;
- horizon;
- invalidation;
- timestamp/data version;
- strategy identity;
- rationale/features resumidas.

## Portfolio Target

Representa exposição desejada após combinação e netting de múltiplas estratégias. Deve impedir estratégias concorrentes de gerar self-cross desnecessário.

## Risk Decision

Resultados possíveis:
- `Approved`;
- `Adjusted`;
- `Rejected`;
- `EmergencyBlocked`.

Com razões estruturadas e políticas que causaram a decisão.

## Eventos críticos

- MarketDataReceived
- MarketDataGapDetected
- OrderBookResynced
- RegimeChanged
- SignalGenerated
- PortfolioTargetCreated
- RiskApproved / RiskAdjusted / RiskRejected
- OrderIntentCreated
- OrderSubmitted
- OrderAccepted
- OrderPartiallyFilled
- OrderFilled
- OrderCanceled
- OrderRejected
- ReconciliationMismatchDetected
- PositionChanged
- CircuitBreakerOpened/Closed
- KillSwitchActivated
- StrategyHealthChanged
- ConfigurationChanged
- LedgerEntryPosted
- BalanceSnapshotReceived
- FeeRecorded
- ValuationUpdated
- ReconciliationAdjustmentPosted

## Invariantes

1. Quantidade enviada respeita filtros da exchange.
2. Exposure pós-ordem não excede Risk Decision autorizada.
3. Uma ordem não é repetida após timeout sem reconciliação.
4. Strategy não acessa ExchangeOrderGateway.
5. Kill switch bloqueia novas exposições imediatamente.
6. Dados stale não podem criar novas entradas.
7. Uma mudança de risco entra em vigor como versão nova e auditável.
8. PnL líquido sempre distingue fees e execução.
9. Todo fill produz postings multi-commodity balanceados por conta/counterparty e referencia order/trade IDs.
10. Fee mantém ativo, quantidade e valuation usados; nunca é agregada sem unidade.
11. Reconciliation corrige projeções por evento compensatório, não por apagamento do histórico.
12. Side effect financeiro nunca precede a persistência durável de intent, policy version e idempotency key.

## Tempo

Persistir timestamps UTC e distinguir `event_time`, `exchange_time`, `received_at`, `processed_at` e `ingested_at` quando aplicáveis. Usar relógio monotônico para medir latência. O serviço de clock deve ser injetável para backtests e testes determinísticos.
