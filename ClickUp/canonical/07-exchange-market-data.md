# 07 - Exchange e Market Data

## Binance first, abstraction always

`IExchangeMarketData`, `IExchangeTrading`, `IExchangeAccount` e `IExchangeReferenceData` isolam a Binance sem reduzir o modelo ao mínimo denominador comum.

## Integração inicial

### REST/WebSocket
- REST para reference data, snapshots e reconciliações.
- WebSocket Streams para market data.
- WebSocket API para User Data Stream e operações quando apropriado.

A arquitetura deve usar a Spot WebSocket API atual para User Data Stream; não criar novo código baseado no fluxo legado de listen-key REST. A referência/changelog oficial deve ser revisada antes de cada implementação.

### Matriz de ambientes

| Ambiente | REST | WS API | Market streams | Credenciais/namespaces |
|---|---|---|---|---|
| Production | `https://api.binance.com/api` | `wss://ws-api.binance.com:443/ws-api/v3` | `wss://stream.binance.com:443/ws` ou `/stream` | separadas, live opt-in |
| Testnet | `https://testnet.binance.vision/api` | `wss://ws-api.testnet.binance.vision/ws-api/v3` | `wss://stream.testnet.binance.vision/stream` | virtuais, reset periódico |
| Demo | `https://demo-api.binance.com/api` | `wss://demo-ws-api.binance.com/ws-api/v3` | `wss://demo-stream.binance.com/ws` ou `/stream` | saldo/reset próprio |

Cada perfil inclui ambiente, endpoints completos, key reference, database namespace, modo de ordens e banner. Combinações inconsistentes devem falhar no startup. Testnet usa somente `/api`; Demo é útil para market data semelhante ao live, mas não é evidência de rentabilidade live.

## SBE e FIX

- JSON/WebSocket é a primeira implementação.
- SBE é fase de performance após baseline.
- SBE schema deve ser versionado e suportar política de depreciação da Binance.
- A cadência e o schema de Diff Depth SBE/FIX SBE devem ser confirmados no changelog oficial no momento da implementação; não hardcodar uma afirmação temporal neste contrato.
- FIX/FIX SBE é evolução, não requisito MVP.

## Local order book

1. iniciar stream;
2. obter snapshot conforme algoritmo oficial;
3. aplicar updates por sequence/update IDs;
4. detectar gap;
5. descartar livro inválido e ressincronizar;
6. bloquear estratégias dependentes do book enquanto inválido.

O consumidor deve respeitar a janela de sequência oficial, o limite de níveis do snapshot e as mudanças de schema/enum. Atualizações duplicadas, fora de ordem ou não representáveis invalidam o livro e exigem resync.

## Quality flags

Cada feed pode estar `Healthy`, `Stale`, `GapDetected`, `Resyncing`, `Disconnected`.

## Rate limits

Implementar limiter central baseado em pesos/ordens/intervalos reportados pela exchange. Peso é contabilizado por IP; unfilled-order count é contabilizado por conta. Registrar headers/contadores, `Retry-After`/`retryAfter`, 403, 429, 418 e 5XX. Evitar retries simultâneos e nunca repetir cegamente uma operação de ordem.

O contrato deve distinguir REST, WebSocket API, market streams e FIX, incluindo limites de conexão/controle, 24h de sessão WS API, `serverShutdown`, 1024 streams por conexão e 300 tentativas de conexão por 5 minutos/IP quando aplicável.

## Precisão e filtros

`exchangeInfo` é dinâmico. O sistema deve aplicar, conforme presentes, `PRICE_FILTER`, `LOT_SIZE`, `MARKET_LOT_SIZE`, `PERCENT_PRICE`, `PERCENT_PRICE_BY_SIDE`, `MIN_NOTIONAL`, `NOTIONAL`, `ICEBERG_PARTS`, `TRAILING_DELTA`, limites de ordens/listas/amends, permissões e status, sem hardcode de precisão por símbolo. Filtros específicos de conta, como `MAX_ASSET`/`myFilters`, devem ser consultados quando aplicáveis.

`CANCEL_ONLY`, `EXPIRED_IN_MATCH`, `TRADE_PREVENTION`, `REPLACED`, `expiryReason` e novas variantes de enum devem ser preservados por um mapper extensível, não descartados como erro desconhecido.

OMS não presume ordem causal entre resposta de submit e User Data Stream: fill/ack pode chegar antes da resposta do comando. Cancel-replace, amend, order lists, STP e SOR podem produzir resultado parcial/ambíguo; cada leg/result é persistido e reconciliado. Comissões preservam ativo e quantidade efetivos.

## User Data Stream

Conta, ordens e fills devem ser eventos prioritários. O fluxo deve declarar autenticação/subscrição (`session.logon`, `userDataStream.subscribe` ou `.subscribe.signature`), `subscriptionId`, `eventStreamTerminated`, `serverShutdown`, ping/pong, rotação de sessão de 24h, resubscribe e reconciliação após reconnect. Falha no stream aciona reconciliação. Nunca esperar apenas polling periódico para descobrir fills.

## Time sync

- sincronização NTP/chrony no host;
- monitorar offset com server time;
- thresholds de warning/stop;
- `timestamp` em unidade explicitamente escolhida (ms ou us);
- serialização/canonicalização e percent-encoding testados antes da assinatura;
- `recvWindow` versionado, dentro do máximo oficial e compatível com a operação real.

## Reconciliation

Executar:
- no startup;
- após reconnect;
- após timeout ambíguo;
- periodicamente;
- sob demanda pelo operador.

Mismatch não explicado bloqueia novas entradas até resolução.

Reconciliação cobre balances `free/locked`, open/all orders no recorte suportado, trades/fills, fees, order lists e ledger/projeções locais. Binance Spot é autoridade externa desses fatos; `Position` e PnL são derivados internos e não devem ser consultados como entidade canônica da exchange.

## Política de doubles

Mocks/fakes são permitidos somente em unit tests, contract tests e replay controlado. Paper usa simulador explícito; integração Testnet usa Binance Testnet real; produção nunca usa fake. Fixtures não podem ser apresentadas como evidência de integração externa.
