# 19 - Roadmap e Releases

Datas são forecast, não compromisso. O desenvolvimento é incremental e a capacidade real será recalibrada em Reviews/Retrospectives.

## R0.0 - Planning Baseline | setembro/2026

Entregas: plano mestre, stack, backlog, DoR/DoD, riscos, ADRs e arquitetura.

## R0.1 - Foundation | set-out/2026

- solution/backend/frontend skeleton;
- CI;
- modules;
- PostgreSQL;
- telemetry baseline;
- Docker multi-arch;
- AWS learning environment inicial.

## R0.2 - Market + OMS Paper Foundation | out-dez/2026

- Binance reference/market data;
- order book;
- User Data Stream;
- OMS/reconciliation;
- Risk Engine v1;
- paper simulator.

**Gate:** migrar runtime para .NET 11 GA assim que publicado e estabilizar antes de qualquer capital real.

## R0.3 - Research Platform | dez/2026-jan/2027

- historical data;
- backtest;
- metrics;
- walk-forward/sensitivity/Monte Carlo;
- baseline Trend/Momentum/Breakout.

## R0.4 - Multi-Strategy | jan-mar/2027

- Mean Reversion/Grid;
- regime detector;
- portfolio netting;
- strategy health;
- execution quality;
- dashboard completo.

## R0.5 - Shadow/Canary | mar-abr/2027

- shadow evidence;
- security hardening;
- disaster/restore drill;
- canary Spot com capital mínimo se gates passarem.

## R1.0 - Stable Spot | alvo mai-jun/2027

Critério: operação Spot observável/reconciliável, risk controls maduros, estratégia validada e histórico de canary suficiente. A data cede ao gate de segurança.

## R1.1 - Futures/Risk | posterior

Futures adapter, margin/leverage/liquidation model, shorts/hedging, funding/basis.

## R1.2 - Market Neutral | posterior

Stat-arb, pairs, multi-exchange research, arbitrage.

## R1.3 - Advanced Execution | posterior

L3 replay, market making, SBE/FIX se benchmarks justificarem.

## R2.x - Adaptive/ML

Somente após existir baseline simples rentável/robusto o suficiente para comparação e volume de dados operacional próprio.
