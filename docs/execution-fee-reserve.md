# Reserva de taxas no sizing

`ExecutionPlanner` aplica uma reserva configurável em basis points sobre o
notional autorizado. Para BUY, o planner só cria um plano quando:

```text
notional autorizado + reserva de taxa < saldo quote livre
```

A comparação é estritamente menor para deixar headroom além da estimativa. A
configuração padrão é `25 bps` (0,25%); o chamador pode fornecer uma
`FeeReservePolicy` explícita no `ExecutionPlanRequest`, com limite de `10.000
bps`. Configurações negativas, acima do limite, valores negativos de notional e
overflow resultam em rejeição/falha fechada.

SELL não depende de saldo quote nem consome a reserva: sua autorização continua
dependendo da exposição/base reconciliada e dos demais gates de risco.

A reserva é uma proteção de sizing, não uma confirmação de taxa final da
exchange. A ordem continua sujeita aos filtros do símbolo, saldo real,
reconciliação, persist-before-submit e aos gates do Worker.

## Comissão efetiva

No ciclo autônomo Testnet, a reserva configurada é comparada com o custo
efetivo obtido em `GET /api/v3/account/commission`. O snapshot versionado
preserva `maker`, `taker`, `buyer`, `seller`, `standard`, `special`, `tax`,
desconto BNB, escopo da conta, instante UTC, fonte e hash canônico.

Snapshot ausente, inválido ou stale bloqueia um BUY. A taxa maker só é usada
porque o planner produz uma tentativa passive-limit; o fallback configurado
continua sendo um piso conservador. Desconto BNB só é aplicado com saldo BNB e
`bookTicker` BNB/quote válido; preço, instante UTC e fonte entram no hash do
snapshot. Sem essa evidência, o ciclo falha fechado.

Contrato verificado em 2026-09-20 na documentação oficial:
`https://developers.binance.com/docs/binance-spot-api-docs/rest-api/account-endpoints`.

## Atribuição após o fill

Antes do submit, a taxa efetiva, hash, fonte e janela UTC do snapshot são
persistidos de modo append-only por `clientOrderId`, na mesma transação do
outbox. O evento `TRADE` recupera essa expectativa e compara o fee efetivo com
o estimado usando `WORKER_AUTONOMY_FEE_DRIFT_TOLERANCE_BPS` (25 bps padrão).

Fee no quote é comparável diretamente; fee no ativo-base usa o preço do fill.
Para BNB, o Worker consulta `GET /api/v3/aggTrades` com `endTime` no instante
do fill e aceita apenas a última negociação anterior dentro de
`WORKER_FEE_ASSET_VALUATION_MAX_AGE_SECONDS` (30 por padrão). O preço, instante
e fonte são persistidos com o fill. Outro terceiro ativo, resposta ausente,
atrasada ou inválida não recebe conversão inventada:
`COMMISSION_VALUATION_UNAVAILABLE` gera alerta crítico, `SafeMode` e
reconciliação read-only, preservando o ledger como fato imutável.
