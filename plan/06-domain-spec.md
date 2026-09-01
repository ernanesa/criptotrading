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

## Entidades/agregados

### Order
State machine controlada pelo OMS. Nunca inferir `Filled` apenas porque uma requisição retornou sucesso.

### Position
Estado consolidado resultante de fills, reconciliado com exchange.

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

## Invariantes

1. Quantidade enviada respeita filtros da exchange.
2. Exposure pós-ordem não excede Risk Decision autorizada.
3. Uma ordem não é repetida após timeout sem reconciliação.
4. Strategy não acessa ExchangeOrderGateway.
5. Kill switch bloqueia novas exposições imediatamente.
6. Dados stale não podem criar novas entradas.
7. Uma mudança de risco entra em vigor como versão nova e auditável.
8. PnL líquido sempre distingue fees e execução.

## Tempo

Persistir timestamps UTC. Usar relógio monotônico para medir latência. O serviço de clock deve ser injetável para backtests e testes determinísticos.
