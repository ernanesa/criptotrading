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
