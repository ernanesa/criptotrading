# 10 - Pesquisa, Backtest e Validação

## Objetivo

Evitar que o laboratório produza estratégias lindas no passado e frágeis no futuro.

## Pipeline obrigatório

Hypothesis → Strategy Spec → Data QA → Backtest → Validation → Out-of-Sample → Walk-Forward → Sensitivity → Monte Carlo/Stress → Paper → Shadow → Canary → Live.

O primeiro experimento é BTC/USDT com uma única baseline simples e uma `UniversePolicy` explícita. A expansão para até 20 ativos exige snapshot point-in-time do universo, capacidade, liquidez e churn budget; não pode usar seleção futura ou survivorship implícito.

Antes de executar busca de parâmetros, registrar hipótese econômica, `experiment_family_id`, variantes planejadas, search space, trial budget, métricas primárias e critérios de rejeição. O holdout final fica bloqueado; cada acesso é auditado. Se houver contaminação, criar nova versão temporal e registrar o motivo — nunca reutilizar o mesmo holdout como se continuasse intocado.

## Backtest fidelity levels

### L1 Candle
Rápido para pesquisa inicial. A Strategy Spec declara a regra conservadora para ambiguidade intrabar; se stop e target puderem ocorrer no mesmo candle, usar pior sequência plausível ou rejeitar a conclusão até fidelidade maior.

### L2 Trade/quote
Spread e execução mais realistas.

### L3 Order-book replay
Para market making, microstructure e estratégias sensíveis a fill/queue.

Não usar L3 para toda pesquisa se L1/L2 já rejeitam a hipótese.

## Custos

Sempre modelar:
- maker/taker fees;
- spread;
- slippage;
- latency;
- funding/borrow futuramente;
- partial fills quando relevante;
- market impact para tamanhos relevantes.
- ativo da comissão, rebates e conversão da fee para a moeda de relatório;
- arredondamento, filtros, saldo disponível e min notional após partial fill.

## Data splits

Nunca random shuffle de séries financeiras para validação temporal simples. Usar splits cronológicos e registrar o instante em que cada feature ficou disponível. Para labels sobrepostos, usar purging/embargo quando aplicável; warmup, revisões/correções, delistings e dados faltantes devem estar definidos antes do experimento.

## Anti-overfitting

Registrar número de experimentos/tentativas. Avaliar:
- parameter stability;
- performance degradation OOS;
- Probability of Backtest Overfitting quando aplicável;
- Deflated Sharpe Ratio;
- multiple-testing/selection bias;
- minimum track-record considerations.
- número total de trials/variantes da família, incluindo resultados descartados;
- holdout access count e experimentos de ablação.

## Walk-forward

Treinar/selecionar apenas em janela passada e avaliar na janela seguinte, repetidamente. Parâmetros só podem usar informação disponível naquele instante.

## Sensitivity

Uma estratégia robusta deve apresentar região de parâmetros aceitável, não um único pico estreito. Heatmaps/surfaces devem fazer parte do relatório.

## Monte Carlo e stress

- reorder/block bootstrap de trades quando metodologicamente válido;
- fees +50/+100%;
- slippage multiplicado;
- gaps/disconnects;
- volatilidade extrema;
- liquidez reduzida;
- sequência adversa de perdas.

## Metrics

PnL líquido, CAGR/return, max DD, Sharpe, Sortino, Calmar, profit factor, expectancy, payoff, win rate, MAE, MFE, turnover, fees, slippage, exposure, time-under-water, capacity/fill ratio e benchmark delta. Cada métrica declara fórmula, sampling frequency, annualization, risk-free rate, unidade e intervalo de confiança/incerteza. Gates usam também limite inferior conservador, não apenas estimativa pontual.

## Experiment Registry

Cada execução persiste:
- strategy/version/commit;
- dataset hash/version;
- período;
- parameters;
- fee model;
- fill model;
- seed;
- environment/tool versions;
- metrics;
- artefatos/resultados;
- decisão: rejected/continue/promote.
- experiment family, trial number/budget e preregistration timestamp;
- holdout access/contamination status;
- container/dependency lock hashes e metric-definition version.

O relatório deve separar PnL bruto, fees, spread, slippage, market impact, latência e PnL líquido, além de registrar benchmark, universo efetivo, número de tentativas e motivo de seleção. Nenhum resultado positivo somente in-sample é promoção.

## Paridade com produção

Backtest, paper, shadow e live reutilizam Strategy, Portfolio, RiskPolicy, filtros, arredondamento e OMS. Apenas clock, market-data source e execution/exchange adapter variam. Um parity suite compara sinais, targets e risk decisions sobre o mesmo replay; divergência exige causa e ADR.

## Benchmarks e ablations

Toda estratégia compara contra `NO_TRADE/cash`, buy-and-hold quando economicamente adequado e baseline simples. Componentes adicionais passam por ablation para demonstrar contribuição líquida após custos. Escolha do benchmark acontece antes de observar o holdout.

## Promotion gates iniciais

Não promover se:
- backtest só é positivo antes de custos;
- OOS falha de forma material;
- drawdown excede política;
- parâmetros são instáveis;
- amostra é pequena demais para conclusão;
- divergência paper/backtest não é explicada;
- estratégia possui risco de cauda incompatível com capital.
