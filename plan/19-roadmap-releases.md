# 19 - Roadmap e Releases

Datas são forecast, não compromisso. O desenvolvimento é incremental e a capacidade real será recalibrada em Reviews/Retrospectives.

Release avança por gate observado, nunca por data. Se evidência, capacidade ou segurança não estiverem prontas, a data desliza e o escopo não pula etapas.

## R0.0 - Planning Baseline | setembro/2026

Entregas: plano mestre, stack, backlog, DoR/DoD, riscos, ADRs, arquitetura e autonomia como requisito central.

Gate adicional: escopo MVP congelado em Spot/BTC/USDT, sem arbitragem/Futures/market making; RAG fica no Research Plane; SafeMode/restore, lineage e rastreabilidade são pré-requisitos, não itens pós-live.

## R0.1 - Foundation | set-out/2026

- solution/backend/frontend skeleton;
- CI;
- modules;
- PostgreSQL;
- telemetry baseline;
- Docker multi-arch;
- runtime local reproduzível em Docker Compose, budgets de recursos e backup/restore documentados;
- SDK .NET 11/C# 15 exato fixado, com matriz de compatibilidade das dependências críticas.

## R0.2 - Financial Core + Market/OMS Foundation | out-dez/2026

- Binance reference/market data;
- order book;
- User Data Stream;
- OMS/reconciliation;
- ledger financeiro por ativo, fee e movimentação;
- Risk Engine v1;
- paper simulator;
- SafeMode/recovery foundation;
- event journal/outbox-inbox e política de durabilidade por classe de evento;
- RiskPolicy versionada;
- restore/reconcile drill inicial.

**Gate:** atualizar o SDK .NET 11 RC pinado para .NET 11 GA em mudança isolada assim que publicado e estabilizar antes de qualquer capital real. Não existe migração de .NET 10, pois a implementação já começa em .NET 11.

## R0.3 - Research Platform | dez/2026-jan/2027

- historical data;
- backtest;
- metrics;
- walk-forward/sensitivity/Monte Carlo;
- baseline Trend Following BTC/USDT;
- experiment/model lineage foundation;
- RAG lexical/semântico local com citações e frescor;
- preregistration, experiment family/trial budget e locked holdout.

## R0.4 - Baseline Paper/Shadow Operável | jan-mar/2027

- baseline Trend em paper e shadow;
- Strategy Health da baseline;
- `NO_TRADE` e safe fallback;
- execução market versus passive limit por política determinística;
- dashboard operacional, risco, pesquisa e decision trace;
- threat model e controle administrativo forte;
- SLOs, alertas e runbooks testados.

## R0.5 - Shadow/Canary Autônomo | mar-abr/2027

- unattended shadow soak tests;
- security hardening;
- disaster/restore drill;
- self-healing/reconciliation tests;
- PromotionReadinessMatrix e CanaryPlan aprovados;
- canary Spot com capital mínimo se gates passarem.

## R1.0 - Stable Autonomous Spot | alvo mai-jun/2027

Critério: operação Spot observável/reconciliável e **sem intervenção humana rotineira** da baseline Trend Following, com `NO_TRADE`, risk controls maduros, recovery seguro e histórico de canary que atenda valores/unidades da `PromotionReadinessMatrix`. A data cede ao gate de segurança.

O R1.0 corresponde ao nível A2: sinal, risk, execução, monitoramento e recuperação automáticos para uma baseline. Não depende de Regime Detection, multi-strategy ou ML para ser operacionalmente autônomo.

### Gate complementar E-19 — A2 decision-complete

Antes de considerar o R1.0 estável, US-190…US-199 devem fechar o loop
market-data→decisão→risco→OMS→reconciliação. Testnet só pode executar com
RiskPolicy ativa, account stream e reconciliation simultâneos, e nenhum estado
`Unknown`. Paper, Shadow e Testnet exigem evidence bundle e rollback ensaiado.

## R1.1 - Multi-Strategy e Regime Adaptativo

- Momentum/Breakout como challengers isolados;
- Regime Detection explicável;
- Portfolio netting;
- seleção, deselegibilidade e alocação bounded;
- estratégia sempre pode resultar em `NO_TRADE`;
- somente estratégias aprovadas individualmente recebem alocação.

## R1.2 - Bounded Continuous Learning

- Model/Strategy Registry completo;
- drift monitoring;
- champion-challenger;
- bounded parameter adaptation;
- contextual-bandit/Bayesian meta-allocation em shadow e canary;
- retraining/validation/promotion/rollback automatizados;
- modelos adaptativos de slippage/regime quando superarem baselines.

## Opções exploratórias sem compromisso de roadmap

Os itens abaixo não pertencem ao produto comprometido. Exigem nova decisão de produto, ADR, threat model, risk model e roadmap próprios.

### Futures/Risk

Futures adapter, margin/leverage/liquidation model, shorts/hedging, funding/basis, mantendo os mesmos boundaries de autonomia e risk.

### Market Neutral

Stat-arb, pairs, multi-exchange research, arbitrage.

### Advanced Execution / Market Making

L3 replay, market making, adverse-selection models, SBE/FIX se benchmarks justificarem.

### Advanced Adaptive Research

- ensembles/Mixture of Experts;
- RL somente quando houver benchmark forte e controles maduros;
- automated strategy discovery em research;
- high availability primary/standby se risco/capital justificarem.

Novas estratégias ou modelos jamais recebem produção automaticamente por simplesmente vencerem um backtest; precisam passar pelo pipeline de evidência e pelos hard risk boundaries.

## Infraestrutura fora do roadmap ativo

AWS, Oracle Cloud e VPS não são marcos das releases R0.x/R1.0. Uma migração futura só recebe release própria após decisão explícita, benchmarks do host local, análise de custo/segurança e ADR aprovado.
