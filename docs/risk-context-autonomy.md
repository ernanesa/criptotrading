# RiskContext do ciclo autônomo

O ciclo autônomo não usa valores sintéticos para decidir exposição. Antes de
avaliar uma barra fechada, ele combina:

- `free` do ativo de cotação na conta Binance como liquidez imediatamente
  disponível (saldo `locked` não é liquidez para uma nova entrada);
- a soma diária, em UTC, apenas das perdas realizadas em
  `trading.execution_fills.realized_pnl_quote`;
- quantidade e custo médio da posição em `trading.position_cost_basis`;
- quantidade/valor atual da conta Binance, comparados com o custo local para
  detectar drift.

O contrato é transportado por `PortfolioSnapshot` e `RiskEvaluationRequest`.
Quando `RiskContextComplete=false`, o avaliador retorna
`RISK_CONTEXT_UNAVAILABLE`: novas entradas são bloqueadas, mas reduções de
posição continuam permitidas pela regra de breaker.

O high-water mark/equity histórico ainda não possui uma projeção durável. Por
isso, o ciclo marca o contexto como incompleto mesmo quando PnL e custo de
posição estão disponíveis. Esse comportamento é intencional e fail-closed;
`Drawdown=0` não significa que o drawdown foi calculado. A implementação do
high-water mark, com reconciliação e teste de restart, é pré-requisito para
autorizar novas entradas autônomas.

Nenhuma chamada deste contexto envia ordem à Binance. A consulta PostgreSQL é
somente leitura e a autorização continua passando pela `RiskPolicy`, pelo
supervisor de runtime e pelo outbox durável.
