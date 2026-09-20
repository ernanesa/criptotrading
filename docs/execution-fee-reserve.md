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
