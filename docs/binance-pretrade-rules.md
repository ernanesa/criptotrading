# Binance Testnet — pre-trade rules boundary

`BinanceSymbolTradingRulesCache` é a fronteira local para as regras públicas de
`exchangeInfo`. Ela normaliza o símbolo, busca as regras por meio de
`IBinanceSpotMarketClient` e mantém cada snapshot por um TTL explícito (máximo de
24 horas). Quando o snapshot expira, uma nova leitura é obrigatória; se a Binance
estiver indisponível ou retornar metadados inválidos, o provider lança
`BinanceTradingRulesUnavailableException` e nunca devolve regras antigas.

`BinancePreTradeValidatedOrderClient` envolve o cliente autenticado isolado. Antes
de cada `SubmitAsync` ele obtém regras frescas e rejeita sem chamar o cliente
interno quando o símbolo não está em `TRADING`, ou quando preço, quantidade,
tick, step ou notional violam os filtros. O resultado contém códigos de violação
determinísticos em `BinancePreTradeValidationException`.

Ordens `MARKET` são rejeitadas nesta fronteira enquanto não houver uma fonte de
preço de referência confiável: sem preço não é possível provar o `MIN_NOTIONAL`
antes do envio. `QueryByClientOrderIdAsync` e `CancelAsync` apenas delegam ao
cliente interno; o wiring no Worker/Testnet deve ser habilitado separadamente,
com aprovação explícita, credenciais novas e `NO_TRADE` desligado somente no
gate controlado correspondente.

Os testes usam somente clientes fake e cobrem cache fresco, expiração, falha de
refresh, bloqueio antes da rede e submissão de uma ordem limite válida. Nenhum
teste envia requisições externas ou ordens reais.
