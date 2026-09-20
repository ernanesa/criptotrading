# Runner de bundle governado

`CryptoTrading.GovernedEvidenceRunner` é a ponte entre o snapshot persistido de
governança e o resultado OOS. Ele:

1. exige `POSTGRES_CONNECTION_STRING` somente no ambiente;
2. consulta o snapshot por `--experiment-id` sem aplicar migrations nem mutar o
   banco;
3. lê um journal público local, aplica o quality gate e executa walk-forward;
4. valida hash/versão do dataset, versão da estratégia, trial budget e estado
   do holdout contra a preregistration;
5. grava o bundle JSON + manifesto imutável local.

Exemplo:

```bash
unset BINANCE_API_KEY BINANCE_API_SECRET
export POSTGRES_CONNECTION_STRING='Host=localhost;Port=5432;Database=cryptotrading;Username=...;Password=...'
dotnet run --project tools/CryptoTrading.GovernedEvidenceRunner -c Release -- \
  --experiment-id 00000000-0000-0000-0000-000000000000 \
  --input /caminho/journal \
  --output /caminho/evidence \
  --dataset-id btcusdt-public-1d \
  --dataset-version 2026-09-19 \
  --dataset-source binance-public-data \
  --symbol BTCUSDT \
  --decision Rejected \
  --trace docs/experiment-governance.md \
  --trace AC-RESEARCH-GOVERNED-01
```

O runner não envia ordens, não acessa endpoints Binance e não concede promoção.
Uma decisão elegível continua sujeita a RiskPolicy, revisão humana e aos gates
de readiness.

O contrato foi exercitado no PostgreSQL efêmero pelo teste
`ReconstructedSnapshotProjectsIntoGovernedEvidenceBundle`: a preregistration e
o trial foram persistidos, o snapshot foi lido por uma nova instância e o
bundle foi escrito somente após as validações de lineage.
