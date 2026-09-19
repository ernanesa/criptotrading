# 12 - Dados e retenção

## Classes de dados

### Transactional
Orders, fills, account ledger, position/PnL projections, balance snapshots, configs, risk policies, users, audits. → PostgreSQL.

### Market history
Candles, trades, book updates. → Parquet particionado; PostgreSQL apenas índices/metadados necessários.

### Research artifacts
Experiment configs, metrics, equity curves, reports. → PostgreSQL + filesystem local/Parquet; object storage é opção futura via adapter.

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

Cada registro temporal preserva, quando disponível, `event_time`, `exchange_time`, `received_at`, `processed_at` e `ingested_at`. Feature e label registram `available_at` para impedir uso antes da disponibilidade real.

### Camadas e imutabilidade

- **Raw/Bronze:** payload ou evento original, append-only, checksum, schema/source version.
- **Normalized/Silver:** tipos, timezone, dedupe e quality flags normalizados, sem perder referência ao raw.
- **Research/Gold:** features, universos e labels derivados com lineage completo.
- **Quarantine:** dados inválidos, ambíguos ou fora de sequência; nunca corrigidos silenciosamente.

Correção tardia cria nova versão/partição e registra `supersedes`; não altera dataset/hash já usado por experimento. Schema evolution exige compatibilidade ou migração explícita.

Desde o primeiro dia de ingestão, capturar snapshots de `exchangeInfo`, filtros, status, listings/delistings observados, symbol migrations e UniversePolicy. Sem esse histórico, universo point-in-time futuro pode ser irrecuperável.

## Ledger, valuation e obrigações externas

- manter ledger multi-commodity de dupla entrada para principal, proceeds, fee/rebate, depósito, retirada, conversão, dust e ajuste;
- registrar comissão no ativo efetivamente cobrado e sua valuation source/time;
- definir cost-basis policy por ambiente e separar cálculo operacional de eventual cálculo fiscal;
- representar stablecoin/quote asset como exposição, incluindo depeg e indisponibilidade;
- importar e reconciliar trades/depósitos/retiradas externos ao bot sem reescrever eventos antigos;
- exportação para obrigações externas deve declarar método, timezone, fontes e limitações; não é aconselhamento fiscal.

## Retenção inicial

- orders/fills/audit: longo prazo;
- configs/risk policies: longo prazo/versionado;
- candles: longo prazo;
- raw trades: conforme espaço e utilidade;
- raw order book: períodos selecionados para pesquisa, não coletar indiscriminadamente;
- telemetry: janela operacional limitada.

## Backup

- PostgreSQL: WAL/PITR ou mecanismo equivalente, dump/base backup + teste de restore;
- Parquet: cópia off-host local/removível quando valioso; object storage apenas se aprovado futuramente;
- configuração Compose/Git versionada, sem secrets; IaC somente para hosting futuro aprovado;
- restore drill antes de canary/live.

Order intent, risk decision, order/fill e ledger financeiro têm objetivo de RPO efetivo zero no processo normal por persistência síncrona/durável. O RPO de backup aplica-se a desastre e não autoriza perda silenciosa desses eventos.

## Portabilidade futura

No ambiente inicial, PostgreSQL, Parquet e artefatos vivem em volumes/filesystem locais com backup e restore testados. Dados devem permanecer exportáveis em formatos padrões e sem dependência de banco proprietário, preservando uma migração futura opcional sem torná-la requisito atual.
