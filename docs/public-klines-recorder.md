# Coleta local de klines públicos

`CryptoTrading.PublicKlinesRecorder` coleta candles Spot históricos do
endpoint público da Binance Spot Testnet (`/api/v3/klines`) e grava um journal
JSONL local com manifesto/fingerprint. O tool não aceita nem usa credenciais,
não chama account/order endpoints e falha se `BINANCE_API_KEY` ou
`BINANCE_API_SECRET` estiverem presentes.

## Execução

Use um diretório novo e limite explícito:

```bash
unset BINANCE_API_KEY BINANCE_API_SECRET
export PATH="$HOME/.dotnet:$PATH"
out="$(mktemp -d /tmp/cryptotrading-klines.XXXXXX)"
dotnet run --project tools/CryptoTrading.PublicKlinesRecorder -c Release -- \
  --symbol BTCUSDT \
  --interval 1d \
  --output "$out" \
  --max-bars 180
```

`--start` e `--end` são opcionais, mas quando usados devem ser timestamps
ISO-8601 UTC. O limite total é 100.000 barras; cada página REST tem no máximo
1.000 registros. Barras cujo `CloseTimeUtc` ainda está no futuro são
descartadas.

O arquivo `dataset-manifest.json` registra origem, parâmetros, quantidade,
paginação e fingerprint do replay. Esse manifesto não é aprovação de edge:
antes do backtest ainda é obrigatório validar disponibilidade/ordenação com
`MarketDataQualityGate`, versionar o dataset, separar treino/OOS e produzir o
bundle experimental com benchmarks.

Para transformar o journal em barras consumíveis pelo backtest, use
`PublicKlineDatasetLoader.Load(...)`. Ele exige eventos `kline-rest`, confere
que o `closeTimeUtc` do payload coincide com o envelope, preserva o fingerprint
do replay e executa o quality gate antes de devolver `ClosedBarEnvelope`.

O tool recusa diretórios não vazios para impedir duplicação silenciosa ou
mistura de datasets de versões diferentes.
