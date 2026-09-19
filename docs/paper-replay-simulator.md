# Simulador Paper/Replay bounded

`CryptoTrading.Exchange.Paper` fornece um simulador determinístico em memória
para Paper e replay local. Ele não possui I/O, não chama a Binance e não envia
ordens para nenhuma exchange.

## Hipóteses explícitas

- O preço executável de compra é o `ask` e o de venda é o `bid`; portanto, o
  spread do tick é aplicado antes do slippage adverso configurado.
- `SubmissionLatency` impede que uma ordem seja preenchida antes do instante
  de elegibilidade. Um tick anterior é ignorado, sem alterar o estado.
- `AvailableQuantity` permite partial fills. Cada fill atualiza o estado da
  ordem e o último fill leva a ordem para `Filled`.
- `MaxSpreadRate`, `MaxOrderQuantity` e `MaxOrderNotional` são limites
  fail-closed. Quando um tick executável viola um limite, a ordem é marcada
  `Rejected`, com `PaperRejectionReason`, e nenhum fill é criado.
- `MaxOpenOrders` e `MaxStoredFills` limitam memória e superfície do replay.
  Exceder o primeiro rejeita o submit; o segundo retém somente os fills mais
  recentes.
- Fee e slippage são registrados por fill. O simulador não presume saldo,
  margem, alavancagem ou liquidação.

## Reprodutibilidade e uso seguro

Use `TimeProvider` manual nos testes e ticks com timestamps UTC. Ordene os
ticks antes do replay e fixe a configuração em um artefato versionado. A
implementação é intencionalmente bounded: não modela impacto de mercado,
book-level execution, latência variável, cancelamento parcial, OHLCV
intrabar ou falhas de rede. Esses pontos permanecem hipóteses de pesquisa e
devem ser adicionados antes de usar resultados para qualquer gate de promoção.

O modo Paper/replay continua separado dos clientes Binance autenticados e do
pipeline live. Nenhuma credencial é necessária para executar esta fatia.
