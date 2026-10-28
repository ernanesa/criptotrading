# Baseline Trend Following — BTCUSDT Spot

## Escopo

Esta é a primeira baseline quantitativa do CryptoTrading. Ela é uma hipótese
reprodutível de pesquisa, não uma promessa de retorno e não envia ordens.
`TrendFollowingStrategy` consome somente candles fechados e produz sinais
`EnterLong`, `ExitLong` ou `NoTrade`.

## Especificação v1.0.0

- Universo: `BTCUSDT` Spot.
- Direção: long-only; fora da posição, o estado implícito é caixa.
- Indicador: cruzamento de médias móveis exponenciais (EMA).
- EMA rápida: 20 candles.
- EMA lenta: 50 candles.
- Entrada: EMA rápida cruza de baixo para cima a EMA lenta no candle fechado.
- Saída: EMA rápida cruza de cima para baixo a EMA lenta no candle fechado.
- Ausência de cruzamento ou histórico insuficiente: `NoTrade`.
- Custo de referência: fee `0,001` + slippage `0,0005` por lado.
- Custo estimado round-trip: `2 * (fee + slippage) = 0,003`.

Os parâmetros de custo fazem parte do fingerprint da configuração. Eles são
assumptions de backtest e não substituem a medição de fill, spread, latência,
liquidez, partial fills e regras da exchange na execução simulada.

## Determinismo e limites

As barras precisam estar em ordem estritamente crescente, em UTC, sem mistura
de símbolos. A EMA inicial é a média simples dos primeiros `N` fechamentos e,
depois, usa `alpha = 2 / (N + 1)`. A decisão do índice `i` usa somente o
fechamento e os indicadores disponíveis até `i`; portanto, nenhum dado futuro é
consultado para emitir um sinal.

O resultado inclui o identificador da estratégia, sua versão, assumptions de
custo, EMAs e motivo estável para auditoria. A configuração pode ser
reproduzida pelo `ConfigurationFingerprint` SHA-256.

## O que ainda não está implícito

Esta baseline não define sizing, stop, take-profit, risco de portfólio,
execução, reconciliação ou critérios de promoção. Esses controles permanecem
nas camadas de risco, OMS, paper exchange e nos gates de validação. Antes de
qualquer uso operacional, o pipeline deve adicionar dados versionados,
backtest com fills realistas, walk-forward, análise de sensibilidade,
stress tests e paper/shadow.

## Slice quantitativo determinístico

`TrendFollowingBacktestEvaluator` fornece uma avaliação bounded para a baseline,
sem enviar ordens e sem buscar dados externos. Ele consome as mesmas barras
fechadas da estratégia e aplica as seguintes hipóteses explícitas:

- long-only, usando todo o caixa disponível na entrada e liquidando toda a
  posição na saída;
- por padrão, fill no fechamento do **candle seguinte** ao sinal, com slippage
  adverso e fee por lado; sinal no último candle não é preenchido;
- posição marcada a mercado pelo fechamento de cada barra;
- turnover é a soma do notional de referência (preço de fechamento vezes
  quantidade) de cada fill, e `TurnoverRatio` divide esse valor pelo capital
  inicial;
- `MaximumDrawdown` é o maior declínio percentual entre um pico de equity e a
  equity subsequente;
- Sharpe usa retornos simples por barra, excesso sobre a taxa livre de risco
  periódica, desvio-padrão amostral e `sqrt(PeriodsPerYear)`. O valor é `null`
  quando não há pelo menos duas observações ou não há variação;
- taxas e slippage são reportados separadamente para impedir que um retorno
  líquido seja confundido com retorno bruto.

O fechamento seguinte é um proxy causal de pesquisa, não uma abertura observada
nem garantia de execução. A hipótese anterior de fill no próprio fechamento do
sinal está **superseded** para novas avaliações: `LegacySignalBarClose` permanece
como opção explícita para reproduzir o timing histórico não causal. A
hipótese é registrada no resultado e no fingerprint de benchmark. Consulte
[`TrendFollowingBacktest.cs`](../src/CryptoTrading.Backtesting/TrendFollowingBacktest.cs)
para o contrato atual.

O modo legado não garante reprodução binária integral das métricas históricas:
o drawdown do benchmark liquidado foi corrigido para incluir custos de saída.
Para recuperar fingerprints e relatórios antigos exatamente, use o commit e os
artefatos originais, sem reclassificá-los como evidência causal.

O fill no fechamento é uma aproximação deliberadamente limitada: não modela
spread, latência, partial fills, rejeições, limites da exchange, OHLCV intra-bar
ou impacto de mercado. O resultado serve para verificar invariantes e comparar
hipóteses reproduzíveis, não para justificar rentabilidade ou promoção.

`EvaluationAssumptionsFingerprint` identifica versão de avaliação, parâmetros,
capital, anualização, taxa livre de risco e hipótese de execução. Não é hash do
dataset nem das janelas. Cada janela tem um hash dos símbolos, timestamps e
fechamentos consumidos; a identidade do conjunto também vincula fronteiras e
resultados, impedindo que uma seleção posterior reutilize o hash original.
O dataset completo tem um hash **declarado**, não verificado nessa rotina; a
vinculação não prova a origem do arquivo nem os demais campos OHLCV. A emissão
de rótulos de elegibilidade pelo writer fica bloqueada até haver verificação
independente do dataset e gates estatísticos.
Resultados históricos sem os novos campos são identificados como timing legado
e fingerprint ausente. Nenhum desses hashes autoriza promoção.

### OOS, walk-forward e sensibilidade

Na hipótese causal, cada janela usa somente o intervalo declarado de treino
para aquecer indicadores e inicia o OOS em caixa, sem executar trades durante
o aquecimento. Um sinal no último candle de treino pode preencher no primeiro
candle OOS; um sinal no último candle OOS não usa preços posteriores. Por ora,
janelas com intervalo entre treino e OOS são rejeitadas: a semântica de embargo
e atualização dos indicadores nesse intervalo ainda precisa ser definida. O modo
legado reproduz a simulação contínua histórica e não deve ser interpretado
como OOS isolado.

Para o modo causal, buy-and-hold entra no primeiro fechamento OOS, sujeito aos
mesmos custos de entrada, e é marcado no fechamento final sem cobrar uma saída
que não ocorreu. O estado aberto/fechado ao final de cada janela é registrado.
A composição de janelas independentes é rejeitada se uma janela anterior
terminou com posição aberta ou estado desconhecido: reiniciar em caixa sem
modelar liquidação e custos produziria um resultado agregado enganoso. Os
resultados por janela continuam disponíveis para análise, sem promoção implícita.

`WalkForwardPlan.Create` apenas produz janelas de treino e teste fora da amostra
com índices explícitos; ele não ajusta parâmetros nem seleciona o melhor
resultado. `SensitivityPlan.Create` valida uma matriz de casos com identificadores
únicos. O consumidor deve executar cada caso com a mesma versão do avaliador,
preservar os hashes de dados/configuração e manter o período OOS separado do
período usado para escolher parâmetros. Nenhuma métrica OOS deve ser misturada
com a amostra de ajuste.

O slice local `SyntheticMarketDataset.CreateBtcUsdtV1` fornece 180 candles
determinísticos, identificados por `synthetic-btcusdt-oos`/`1.0.0`, com hash
SHA-256 e origem explicitamente sintética. Ele existe para validar o pipeline e
os invariantes de reprodutibilidade; não representa o mercado e não deve ser
usado para inferir retorno.

`WalkForwardEvaluator` usa as barras de treino apenas para aquecer as EMAs e
calcula as métricas exclusivamente nas barras OOS de cada janela. Ele não
otimiza parâmetros, não escolhe o melhor caso, não acessa a Binance e nunca
envia ordens. A evidência mínima esperada é reproduzir o mesmo hash do dataset,
fingerprint da configuração e resultados por janela antes de qualquer estudo
com dados reais versionados.
