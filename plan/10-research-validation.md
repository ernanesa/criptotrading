# 10 - Pesquisa, Backtest e Validação

## Objetivo

Evitar que o laboratório produza estratégias lindas no passado e frágeis no futuro.

## Pipeline obrigatório

Hypothesis → Strategy Spec → Data QA → Backtest → Validation → Out-of-Sample → Walk-Forward → Sensitivity → Monte Carlo/Stress → Paper → Shadow → Canary → Live.

O primeiro experimento é BTC/USDT com uma única baseline simples e uma `UniversePolicy` explícita. A expansão para até 20 ativos exige snapshot point-in-time do universo, capacidade, liquidez e churn budget; não pode usar seleção futura ou survivorship implícito.

## Backtest fidelity levels

### L1 Candle
Rápido para pesquisa inicial. Conservative intrabar assumptions.

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

PnL líquido, CAGR/return, max DD, Sharpe, Sortino, Calmar, profit factor, expectancy, payoff, win rate, MAE, MFE, turnover, fees, slippage, exposure, time-under-water e benchmark delta.

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

O relatório deve separar PnL bruto, fees, spread, slippage, market impact, latência e PnL líquido, além de registrar benchmark, universo efetivo, número de tentativas e motivo de seleção. Nenhum resultado positivo somente in-sample é promoção.

## Promotion gates iniciais

Não promover se:
- backtest só é positivo antes de custos;
- OOS falha de forma material;
- drawdown excede política;
- parâmetros são instáveis;
- amostra é pequena demais para conclusão;
- divergência paper/backtest não é explicada;
- estratégia possui risco de cauda incompatível com capital.
