# Estado operacional da API

`GET /operational/status` expõe um snapshot somente leitura do plano
operacional mantido pelo processo da API. O endpoint não aceita comandos, não
acessa a Binance e não representa automaticamente o estado interno do Worker,
do PostgreSQL ou da exchange.

Exemplo local:

```bash
curl -s http://127.0.0.1:5080/operational/status
```

O campo `mutatingEndpointsEnabled` permanece `false`. Qualquer endpoint futuro
de kill switch/reset deverá exigir autenticação forte, autorização independente,
idempotência, auditoria durável e confirmação explícita de escopo.
