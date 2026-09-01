# 12 - Dados e retenção

## Classes de dados

### Transactional
Orders, fills, positions, balances snapshots, configs, risk policies, users, audits. → PostgreSQL.

### Market history
Candles, trades, book updates. → Parquet particionado; PostgreSQL apenas índices/metadados necessários.

### Research artifacts
Experiment configs, metrics, equity curves, reports. → PostgreSQL + object storage/Parquet.

### Telemetry
Metrics/logs/traces com retenção própria e menor duração que ledger financeiro.

## PostgreSQL

Baseline: PostgreSQL 18.x atual. Usar migrations. Evitar gravação de todo tick via EF Core. Para bulk, usar Npgsql/COPY e batches.

## Parquet

Particionar por source/symbol/data_type/date; compressão apropriada; metadados incluem timezone, schema version e source.

## DuckDB

Ferramenta de research para consultar Parquet sem criar infraestrutura distribuída.

## Data quality

Cada dataset deve registrar:
- source;
- download/ingestion time;
- gaps;
- duplicates;
- schema;
- symbol/filter metadata;
- hash/version;
- corrections.

## Retenção inicial

- orders/fills/audit: longo prazo;
- configs/risk policies: longo prazo/versionado;
- candles: longo prazo;
- raw trades: conforme espaço e utilidade;
- raw order book: períodos selecionados para pesquisa, não coletar indiscriminadamente;
- telemetry: janela operacional limitada.

## Backup

- PostgreSQL: dump/base backup + teste de restore;
- Parquet: cópia em object storage quando valioso;
- configuração IaC/Git já versionada, sem secrets;
- restore drill antes de canary/live.

## Migração cloud

Dados devem ser exportáveis em formatos padrões. Evitar dependência de banco proprietário para permitir AWS → Oracle/VPS.
