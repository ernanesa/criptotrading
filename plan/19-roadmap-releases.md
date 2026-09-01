# 19 - Roadmap e Releases

Datas são forecast, não compromisso. O desenvolvimento é incremental e a capacidade real será recalibrada em Reviews/Retrospectives.

## R0.0 - Planning Baseline | setembro/2026

Entregas: plano mestre, stack, backlog, DoR/DoD, riscos, ADRs, arquitetura e autonomia como requisito central.

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
- paper simulator;
- SafeMode/recovery foundation.

**Gate:** migrar runtime para .NET 11 GA assim que publicado e estabilizar antes de qualquer capital real.

## R0.3 - Research Platform | dez/2026-jan/2027

- historical data;
- backtest;
- metrics;
- walk-forward/sensitivity/Monte Carlo;
- baseline Trend/Momentum/Breakout;
- experiment/model lineage foundation.

## R0.4 - Autonomous Multi-Strategy | jan-mar/2027

- Mean Reversion/Grid;
- regime detector + confidence;
- Autonomy Orchestrator;
- automatic strategy eligibility/ranking;
- dynamic risk-budget allocation;
- portfolio netting;
- Strategy Health;
- automatic NO_TRADE;
- execution quality/adaptive maker-vs-taker;
- dashboard completo e decision trace.

## R0.5 - Shadow/Canary Autônomo | mar-abr/2027

- unattended shadow soak tests;
- automatic regime transitions/strategy switching;
- security hardening;
- disaster/restore drill;
- self-healing/reconciliation tests;
- canary Spot com capital mínimo se gates passarem.

## R1.0 - Stable Autonomous Spot | alvo mai-jun/2027

Critério: operação Spot observável/reconciliável e **sem intervenção humana rotineira**, com seleção/alocação automática entre estratégias validadas, `NO_TRADE`, risk controls maduros, recovery seguro, estratégia(s) validadas e histórico de canary suficiente. A data cede ao gate de segurança.

O R1.0 usa adaptação bounded/explicável. Ele já deve reagir sozinho a regime, Strategy Health e performance, mas não depende de ML complexo para ser autônomo.

## R1.1 - Continuous Learning

- Model/Strategy Registry completo;
- drift monitoring;
- champion-challenger;
- bounded parameter adaptation;
- contextual-bandit/Bayesian meta-allocation em shadow e canary;
- retraining/validation/promotion/rollback automatizados;
- modelos adaptativos de slippage/regime quando superarem baselines.

## R1.2 - Futures/Risk

Futures adapter, margin/leverage/liquidation model, shorts/hedging, funding/basis, mantendo os mesmos boundaries de autonomia e risk.

## R1.3 - Market Neutral

Stat-arb, pairs, multi-exchange research, arbitrage.

## R1.4 - Advanced Execution / Market Making

L3 replay, market making, adverse-selection models, SBE/FIX se benchmarks justificarem.

## R2.x - Advanced Adaptive Research

- ensembles/Mixture of Experts;
- RL somente quando houver benchmark forte e controles maduros;
- automated strategy discovery em research;
- high availability primary/standby se risco/capital justificarem.

Novas estratégias ou modelos jamais recebem produção automaticamente por simplesmente vencerem um backtest; precisam passar pelo pipeline de evidência e pelos hard risk boundaries.
