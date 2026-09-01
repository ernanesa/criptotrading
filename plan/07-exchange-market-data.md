# 07 - Exchange e Market Data

## Binance first, abstraction always

`IExchangeMarketData`, `IExchangeTrading`, `IExchangeAccount` e `IExchangeReferenceData` isolam a Binance sem reduzir o modelo ao mínimo denominador comum.

## Integração inicial

### REST/WebSocket
- REST para reference data, snapshots e reconciliações.
- WebSocket Streams para market data.
- WebSocket API para User Data Stream e operações quando apropriado.

A Binance removeu em 2026 o fluxo legado de listen-key por endpoints REST; não criar arquitetura baseada em tutoriais antigos.

## SBE e FIX

- JSON/WebSocket é a primeira implementação.
- SBE é fase de performance após baseline.
- SBE schema deve ser versionado e suportar política de depreciação da Binance.
- Em agosto/2026, diff-depth SBE e FIX SBE passaram a 20 ms.
- FIX/FIX SBE é evolução, não requisito MVP.

## Local order book

1. iniciar stream;
2. obter snapshot conforme algoritmo oficial;
3. aplicar updates por sequence/update IDs;
4. detectar gap;
5. descartar livro inválido e ressincronizar;
6. bloquear estratégias dependentes do book enquanto inválido.

## Quality flags

Cada feed pode estar `Healthy`, `Stale`, `GapDetected`, `Resyncing`, `Disconnected`.

## Rate limits

Implementar limiter central baseado em pesos/ordens/intervalos reportados pela exchange. Evitar retries simultâneos. Respeitar 429/418 e retry-after.

## Precisão e filtros

`exchangeInfo` é dinâmico. O sistema deve aplicar filtros de preço, lote, notional e status antes do envio, sem hardcode de precisão por símbolo.

## User Data Stream

Conta, ordens e fills devem ser eventos prioritários. Falha no stream aciona reconciliação. Nunca esperar apenas polling periódico para descobrir fills.

## Time sync

- sincronização NTP/chrony no host;
- monitorar offset com server time;
- thresholds de warning/stop;
- `recvWindow` mínimo compatível com operação real.

## Reconciliation

Executar:
- no startup;
- após reconnect;
- após timeout ambíguo;
- periodicamente;
- sob demanda pelo operador.

Mismatch não explicado bloqueia novas entradas até resolução.
