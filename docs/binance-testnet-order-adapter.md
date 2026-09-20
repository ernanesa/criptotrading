# Adaptador de ordens Spot Binance Testnet

`BinanceSpotOrderClient` é um adaptador isolado para a próxima etapa de
validação. Ele não é registrado no `Api` ou no `Worker` e não é chamado por
nenhum fluxo atual.

## Guardrails

- aceita somente `BinanceEnvironment.Testnet` e `https://testnet.binance.vision/api/`;
- `EnableOrderExecution` é `false` por padrão; submit/cancel falham antes de
  criar uma requisição até que uma configuração Testnet explicitamente aprovada
  o habilite;
- valida símbolo, quantidade, preço, tipo, time-in-force e `clientOrderId`;
- assina cada requisição com HMAC-SHA-256 e não registra segredo;
- sincroniza o relógio via endpoint público `/api/v3/time` antes de cada
  requisição assinada e recusa drift fora da tolerância configurada;
- `SubmitAsync` não possui retry. Timeout, cancelamento durante transporte ou
  erro de transporte/servidor 5xx resultam em
  `BinanceOrderSubmissionAmbiguousException`;
- uma submissão ambígua exige `QueryByClientOrderIdAsync` antes de qualquer
  decisão de reenvio;
- query é `GET`; submit é `POST`; cancel é `DELETE` em `/api/v3/order`.

Este contrato não autoriza produção, não envia ordens durante testes e ainda
não constitui aprovação para conectar o adaptador ao Worker.
