# RiskContext do ciclo autônomo

O ciclo autônomo não usa valores sintéticos para decidir exposição. Antes de
avaliar uma barra fechada, ele combina:

- `free` do ativo de cotação na conta Binance como liquidez imediatamente
  disponível (saldo `locked` não é liquidez para uma nova entrada);
- a soma diária, em UTC, apenas das perdas realizadas em
  `trading.execution_fills.realized_pnl_quote`;
- quantidade e custo médio da posição em `trading.position_cost_basis`;
- quantidade/valor atual da conta Binance, comparados com o custo local para
  detectar drift;
- equity marcada apenas com BTC/USDT, persistida por escopo de estratégia em
  `trading.autonomy_equity_high_water_marks`, com observações append-only para
  reconstrução e auditoria após reinício.

O contrato é transportado por `PortfolioSnapshot` e `RiskEvaluationRequest`.
Quando `RiskContextComplete=false`, o avaliador retorna
`RISK_CONTEXT_UNAVAILABLE`: novas entradas são bloqueadas, mas reduções de
posição continuam permitidas pela regra de breaker.

O primeiro valuation cria o high-water mark e permanece incompleto por desenho:
é necessário observar uma segunda valuation coerente antes de autorizar novas
entradas. Depois disso, o mark só pode subir; `drawdown` é calculado como
`max(highWaterMark - equity, 0)` na moeda de cotação. Valuation fora de ordem,
conflitante, com ativos não suportados ou indisponível mantém o contexto
incompleto e bloqueia novas entradas. A projeção e as observações são escritas
na mesma transação que a leitura contábil do ciclo.

Nenhuma chamada deste contexto envia ordem à Binance. A consulta PostgreSQL é
somente leitura e a autorização continua passando pela `RiskPolicy`, pelo
supervisor de runtime e pelo outbox durável.

Até o ledger representar reservas por ordem e ativo, a reconciliação não pode
inferir equivalência entre o total local e as parcelas `free`/`locked` da
exchange. Qualquer parcela `locked` não zero é registrada como
`EXCHANGE_LOCKED_BALANCE_UNMODELED`, gera mismatch e força `SafeMode`, mesmo se
os totais coincidirem. Essa trava é intencionalmente conservadora e só deve ser
relaxada após implementar e testar reservas, fees, fills parciais e liberação
de bloqueios por ordem.
