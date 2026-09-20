# Runner OOS público local

`CryptoTrading.PublicOosRunner` consome um journal criado pelo
`PublicKlinesRecorder`, executa `PublicKlineDatasetLoader` + quality gate e
roda o baseline Trend Following em walk-forward (`60` barras de treino, `20`
OOS, passo `20`). O relatório contém benchmarks por janela, resumo composto,
fingerprints e `promotion_authorized=false`.

```bash
unset BINANCE_API_KEY BINANCE_API_SECRET
dotnet run --project tools/CryptoTrading.PublicOosRunner -c Release -- \
  --input /caminho/do/journal \
  --output /caminho/do/oos-report.json \
  --dataset-id btcusdt-public-1d \
  --version 2026-09-19 \
  --source binance-public-data \
  --symbol BTCUSDT
```

O runner não preregistra experimento, não acessa holdout e não autoriza
promoção. O relatório só pode entrar no `ExperimentEvidenceWriter` depois de
preregistration, budget, holdout e referências de rastreabilidade válidos.

Uma execução operacional validada em 2026-09-19 processou 179 barras públicas,
gerou 5 janelas OOS e confirmou igualdade entre o fingerprint do manifesto e o
fingerprint lido após reinício do journal. O resultado permaneceu explicitamente
não-promocional.

Para repetir a coleta pública e o walk-forward em um único gate descartável,
execute `scripts/test-public-oos-production.sh`. O harness usa um diretório
temporário, recusa credenciais presentes no ambiente, coleta no máximo 180
barras, valida o schema/fingerprint do relatório e exige
`promotion_authorized=false`; nenhum dataset ou relatório temporário é
versionado.
