# 09 - Gestão de risco

## Princípio

O Risk Engine é independente e possui autoridade para reduzir/rejeitar qualquer target. Estratégia não pode elevar limites.

## Camadas

### Trade risk
- risk per trade;
- stop/invalidation;
- max slippage;
- min expected edge;
- max order size.

### Strategy risk
- risk budget;
- rolling drawdown;
- consecutive losses;
- strategy health;
- turnover/fee budget.

### Asset risk
- max exposure;
- liquidity/spread;
- volatility;
- concentration.

### Portfolio risk
- total gross/net exposure;
- correlated exposure;
- beta/factor exposure futuro;
- daily loss;
- maximum drawdown;
- tail stress.

### Operational risk
- data stale;
- WebSocket down;
- exchange errors/rate limit;
- clock drift;
- reconciliation mismatch;
- database/queue pressure.

## Position sizing

Baseline: fixed fractional/volatility-aware. Tamanho deriva de capital em risco e distância até invalidação. Fractional Kelly pode ser pesquisado; full Kelly não será padrão.

## Protections

Inspiradas em frameworks maduros, porém centralizadas:
- StopLossStreakProtection;
- MaxDrawdownProtection;
- DailyLossProtection;
- CooldownProtection;
- LowProfitAssetProtection;
- VolatilityProtection;
- SpreadProtection;
- LiquidityProtection;
- MarketDataHealthProtection;
- ClockDriftProtection;
- ExchangeHealthProtection;
- CorrelationProtection;
- StrategyHealthProtection.

## Circuit breakers

Escopos: strategy, symbol, exchange e global. Um breaker deve registrar causa, threshold, evidência e condição de reset.

## Kill switch

Ao ativar:
1. bloquear novas exposições;
2. suspender geração acionável de sinais;
3. cancelar ordens abertas conforme política;
4. opcionalmente reduzir/zerar posições apenas se configuração explícita;
5. persistir incidente;
6. alertar operador.

Kill switch não depende de Strategy Engine saudável.

## Limites iniciais

Valores percentuais definitivos NÃO serão hardcoded neste documento. Antes de paper com decisão acionável, preencher e aprovar `templates/risk-policy.md`; antes de canary, preencher `templates/promotion-readiness-matrix.md` e `templates/canary-plan.md`. Cada artefato define valores, validade, evidência e aprovador. Canary usa envelope numericamente menor que live normal; “menor” sem valor e unidade não é gate válido.

## RiskPolicy versionada

Cada policy deve persistir versão, ambiente, capital de referência, autor/aprovador, validade, hash e motivo da mudança. O contrato deve cobrir, no mínimo:

- exposição/notional por ativo, estratégia e portfólio;
- risco por trade, daily loss, drawdown e losing streak;
- spread, slippage, volatilidade, liquidez e capacidade;
- stale market data, clock drift, exchange health e reconciliação;
- turnover/churn e orçamento de fees;
- ações de breaker, condição de reset/ack e política de cancelamento;
- fallback `NO_TRADE`, bloqueio de novas entradas e comportamento de redução/saída.

Nenhum learner, selector ou operador não autorizado pode aumentar hard limits ou remover um breaker. A policy deve ser avaliada antes de cada ordem e sua versão deve acompanhar a decisão e o audit event.

## Valuation e estado financeiro

Limites de exposição, drawdown e daily loss declaram moeda de referência, valuation source, timestamp/freshness e comportamento quando o preço não é confiável. Fees em ativo diferente, stablecoin depeg, depósito/retirada externa, dust e reconciliation adjustment entram no cálculo. Estado financeiro incerto resulta em `NO_NEW_ENTRIES`/`SafeMode`, não em estimativa otimista.

## Kill switch e rollback

O kill switch deve possuir caminho independente de Strategy e, quando viável, da UI principal. Sua policy define autenticação/step-up, escopo, tratamento de ordens em voo, cancelamento, comportamento de posições, acknowledgement, reset e teste periódico. Rollback de software/modelo não pode ignorar ordens abertas nem assumir que a versão anterior continua adequada ao regime atual; fallback seguro pode ser `NO_TRADE`.

## Risk of ruin

Relatórios de promoção devem incluir distribuição de drawdown, losing streak e estimativa de ruin/stress sob custos e slippage adversos.
