# 08 - Catálogo de estratégias

## Política

Uma estratégia é uma hipótese testável, não uma promessa. Cada uma precisa de `Strategy Spec`, regime-alvo, custos esperados, failure modes e gates de promoção.

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
- ML para regime, volatilidade, slippage, filtering e allocation;
- reinforcement learning apenas em sandbox experimental;
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

## Strategy Health

Inputs:
- rolling expectancy;
- rolling Sharpe/Sortino;
- drawdown;
- slippage degradation;
- regime mismatch;
- frequência de stop;
- estabilidade de features.

Estados: HEALTHY → DEGRADED → SUSPICIOUS → DISABLED. Reabilitação exige critérios explícitos, não simples passagem do tempo.
