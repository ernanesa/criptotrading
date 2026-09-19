# Recorder e replay de market data

O módulo `CryptoTrading.Backtesting` possui um recorder local para dados
públicos de mercado. A implementação é deliberadamente limitada ao journal
append-only em JSONL; ela não autentica na Binance, não grava credenciais e não
envia ordens.

## Formato

Cada evento ocupa uma linha em `market-data-YYYYMMDD.jsonl`, particionada pela
data UTC do evento. O envelope versionado contém:

- `schemaVersion`: versão do contrato (`1` atualmente);
- `sequence`: sequência monotônica atribuída pelo recorder;
- `eventTimeUtc` e `receivedAtUtc`: timestamps UTC explícitos;
- `eventType`, `stream` e `symbol`;
- `payload`: JSON do evento público normalizado;
- `checksum`: SHA-256 do envelope canônico sem o próprio checksum.

Qualquer JSON inválido, versão desconhecida, timestamp não UTC, sequência
negativa ou checksum divergente interrompe o replay com erro. Isso evita que um
dataset parcialmente corrompido seja tratado como evidência de backtest.

## Uso

```csharp
using var recorder = new MarketDataRecorder("artifacts/market-data");

await recorder.AppendAsync(
    eventTimeUtc: eventReceived.EventTimeUtc,
    receivedAtUtc: eventReceived.ReceivedAt,
    eventType: "trade",
    stream: eventReceived.Stream,
    symbol: eventReceived.Symbol,
    payload: new { eventReceived.TradeId, eventReceived.Price, eventReceived.Quantity });

var replay = await recorder.ReadReplayAsync(
    fromUtc: windowStartUtc,
    throughUtc: windowEndUtc);

// replay.Records é ordenado por eventTimeUtc e, em empate, sequence.
// replay.DatasetChecksum identifica exatamente o conteúdo reproduzido.
```

O recorder deve ter um único escritor por diretório. Leitores podem executar
em paralelo, inclusive enquanto o arquivo está sendo anexado. O diretório
deve ficar fora do Git e pode conter dados que exigem controle de acesso local.

## Retenção

Retenção não é aplicada implicitamente. O operador precisa fornecer uma
política explícita de idade máxima e orçamento total:

```csharp
var result = await recorder.ApplyRetentionAsync(
    new MarketDataRetentionPolicy(
        maximumAge: TimeSpan.FromDays(14),
        maximumBytes: 10L * 1024 * 1024 * 1024),
    DateTimeOffset.UtcNow);
```

O processo remove primeiro arquivos expirados e, depois, os arquivos mais
antigos até o limite de bytes. Antes de reduzir a retenção, exporte ou
versione o dataset necessário para a validação OOS. O fingerprint retornado
por `ReadReplayAsync` deve ser registrado na evidência de backtest.

## Limites desta etapa

Este slice ainda não é um pipeline de produção: não há compressão/Parquet,
upload remoto, catálogo de datasets, rotação automática ou integração de
replay com uma estratégia. Essas evoluções só devem ser feitas depois de
validar o JSONL, a ordenação, a verificação de integridade e a política de
retenção com datasets públicos reproduzíveis.
