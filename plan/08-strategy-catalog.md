# 08 - Catálogo de estratégias

## Política

Uma estratégia é uma hipótese testável, não uma promessa. Cada uma precisa de `Strategy Spec`, regime-alvo, custos esperados, failure modes e gates de promoção.

A plataforma não depende de uma única estratégia. O `Autonomy Orchestrator` escolhe, combina ou rejeita automaticamente estratégias previamente validadas segundo contexto, utilidade esperada, risco, custos, incerteza e Strategy Health.

## Tier A - Implementar antes do 1.0

### Trend Following
Captura movimentos persistentes; variantes EMA/Donchian/volatility-adjusted. Evitar empilhar indicadores redundantes.

### Momentum
Time-series momentum com filtros de volatilidade/liquidez.

### Breakout
Rompimento de faixa/volatilidade com confirmação e stop por invalidação.

### Mean Reversion
Z-score/VWAP/Bollinger, podendo incorporar microestrutura. Só habilitada em regimes compatíveis.

### Adaptive Grid
Arithmetic/geometric/dynamic spacing guiado por volatilidade e regime; limites rígidos de inventário/capital.

## Tier B - Pós-1.0

### Adaptive DCA
Time/price/signal/volatility adjusted com hard cap de exposição. Nunca DCA infinito.

### Composite/COMBO-like
Direção + grid + entradas graduais + trailing exit, prioritariamente após Futures.

### Loop/Rebalancing
Ciclos Spot controlados por condição de lucro e risco.

### Market Making
Pure/Dynamic/Inventory-aware/Volatility-aware; estudar Avellaneda-Stoikov. Requer order book simulator e adverse-selection metrics.

### Statistical Arbitrage
Pairs, cointegration, spread z-score, PCA residuals, relative value.

### Hedging
Redução de beta/exposição sistêmica via derivativos quando Futures estiver habilitado.

### Funding Arbitrage
Spot/perpetual delta-neutral quando retorno líquido de funding exceder custos e riscos.

### Basis / Cash-and-Carry
Captura de convergência Spot/Futures.

### Triangular Arbitrage
Ciclos dentro de uma exchange com cálculo líquido completo e execução atômica/risco de perna.

### Cross-Exchange Arbitrage / XEMM
Somente após adapter multi-exchange, capital pré-posicionado e infraestrutura adequada.

## Tier C - Pesquisa avançada

- ensemble de alphas;
- mixture of experts por regime;
- contextual bandit/Bayesian allocation entre estratégias validadas;
- ML para regime, volatilidade, slippage, filtering e allocation;
- reinforcement learning apenas em sandbox experimental até provar vantagem sobre métodos mais simples;
- LP em DEX somente como produto separado/futuro.

## Regime matrix inicial

| Regime | Preferência |
|---|---|
| Strong Bull | Momentum, Breakout, Trend |
| Bull | Trend, Pullback |
| Range Low Vol | Grid, Mean Reversion, Market Making futuro |
| Range High Vol | Mean Reversion seletiva, Adaptive Grid reduzido |
| Bear | Cash no Spot; shorts/hedges somente após Futures |
| Panic/Extreme Vol | Reduzir ou NO TRADE |
| Low Liquidity | NO TRADE ou tamanho mínimo |

A matriz é apenas o bootstrap explicável. Com dados suficientes, o selector passa a aprender pesos condicionais dentro dos mesmos limites de elegibilidade e risco.

## Strategy Selection

Etapas obrigatórias:
1. filtrar por compatibilidade de regime;
2. remover estratégias `SUSPICIOUS/DISABLED`;
3. estimar edge líquido após custos;
4. penalizar drawdown, incerteza, correlação e deterioração de execução;
5. ranquear candidatos;
6. alocar risk budget;
7. escolher `NO_TRADE` se nenhuma alternativa for adequada.

Troca de estratégia deve usar confidence + hysteresis/cooldown para evitar churn em transições ruidosas.

## Strategy Health

Inputs:
- rolling expectancy;
- rolling Sharpe/Sortino;
- drawdown;
- slippage degradation;
- regime mismatch;
- frequência de stop;
- estabilidade de features;
- drift de distribuição e conceito;
- divergência backtest/paper/shadow/live.

Estados: HEALTHY → DEGRADED → SUSPICIOUS → DISABLED. Reabilitação exige critérios explícitos, não simples passagem do tempo.

O estado influencia automaticamente elegibilidade e risk budget.

## Autoaprendizado

A estratégia live não modifica livremente seu código. Aprendizado ocorre por:
- atualização de scores/posteriors;
- parâmetros dentro de envelopes aprovados;
- retraining de modelos fora do hot path;
- champion-challenger;
- paper/shadow/canary;
- promoção/rollback governados por critérios objetivos.

Novos algoritmos gerados por pesquisa ou IA são tratados como candidatos novos e passam por todo o pipeline de validação.
