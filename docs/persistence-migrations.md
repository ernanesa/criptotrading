# Persistência local e migrations

O projeto `CryptoTrading.Persistence` entrega o contrato SQL versionado para a
primeira fatia durável: journal append-only, postings do ledger, projeção de
saldos e inbox/outbox idempotentes. Os scripts são embarcados no assembly e
executados pelo adapter `PostgresTradingStore`, que usa Npgsql, locks
consultivos do PostgreSQL e comandos parametrizados.

## Aplicação

O caminho recomendado em runtime é criar `PostgresTradingStore` com uma
connection string fornecida pela configuração local e chamar
`ApplyMigrationsAsync`. Cada migration é executada em sua própria transação,
com `pg_advisory_xact_lock`, e a tabela `trading.schema_migrations` impede
reaplicação. A senha deve vir do ambiente local e nunca deve ser colocada no
comando, no Git ou em tickets:

```csharp
await using var store = new PostgresTradingStore(
    new PostgresPersistenceOptions(connectionString));
await store.ApplyMigrationsAsync(cancellationToken);
```

Para uma inspeção manual, os scripts `.up.sql` também podem ser aplicados em
ordem lexicográfica, dentro de uma transação por migration:

```bash
find src/CryptoTrading.Persistence/Migrations -name '*up.sql' -print | sort
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -f src/CryptoTrading.Persistence/Migrations/001_initial_schema.up.sql
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 \
  -f src/CryptoTrading.Persistence/Migrations/002_append_only_guards.up.sql
```

Confirme a versão aplicada sem imprimir a string de conexão:

```sql
SELECT version, applied_at
FROM trading.schema_migrations
ORDER BY version;
```

Os triggers de balanceamento são `DEFERRABLE INITIALLY DEFERRED`: a aplicação
deve inserir o journal e todos os postings na mesma transação. Journal e
postings não podem ser atualizados ou removidos; correções devem ser novos
eventos compensatórios.

## Rollback

Rollback é uma operação destrutiva e não é parte do fluxo normal. Faça backup e
verifique a restauração antes de executar um `.down.sql`. Para remover apenas
as proteções da migration 002, execute seu down script em uma transação; para
remover o schema inteiro, o down da 001 usa `DROP SCHEMA ... CASCADE` e apaga
todos os dados locais.

Antes de qualquer rollback:

```bash
pg_dump "$DATABASE_URL" --format=custom --file=backups/cryptotrading-$(date +%Y%m%d%H%M%S).dump
```

O runner definitivo deve adquirir um advisory lock, registrar a versão apenas
após sucesso, rejeitar gaps de versão e não executar downs automaticamente.

## Operações duráveis

- `AppendJournalEntryAsync` grava o journal, postings e a projeção de saldo na
  mesma transação serializável. Repetições do mesmo ID ou idempotency key só
  são aceitas quando o conteúdo é idêntico.
- `EnqueueAsync` persiste o comando antes de qualquer I/O de exchange e aplica
  a mesma regra de idempotência.
- `ClaimOutboxAsync` usa `FOR UPDATE SKIP LOCKED`, incrementa tentativas e
  atribui um `lease_owner` com expiração. `MarkOutboxProcessedAsync` e
  `FailOutboxAsync` exigem o dono do lease; falhas liberam a mensagem para
  retry em `available_at`.
- `GetBalanceAsync` consulta a projeção reconstruível; o journal permanece a
  fonte de verdade.

## Ativação pelo Worker

O Worker registra o `PostgresTradingStore` somente quando
`WORKER_PERSISTENCE_ENABLED=true`. As migrations também exigem a flag explícita
`WORKER_APPLY_MIGRATIONS=true`; portanto, iniciar o processo com defaults não
abre conexão nem altera o banco. A connection string deve ser fornecida por
`POSTGRES_CONNECTION_STRING` em ambiente local e nunca deve ser commitada.

O dispatcher usa `FOR UPDATE SKIP LOCKED`, lease por mensagem e retry com
backoff exponencial limitado. O executor registrado nesta etapa é PAPER; o
default `WORKER_NO_TRADE=true` reconhece mensagens sem executar ordens. Não há
adapter Binance privado conectado ao dispatcher.

Exemplo de ativação local, depois de iniciar o Compose:

```bash
export POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=cryptotrading;Username=cryptotrading;Password=<senha-local>'
export WORKER_PERSISTENCE_ENABLED=true
export WORKER_APPLY_MIGRATIONS=true
export WORKER_EXECUTION_MODE=PAPER
export WORKER_NO_TRADE=true
dotnet run --project src/CryptoTrading.Worker/CryptoTrading.Worker.csproj
```

Nunca use credenciais da Binance para autenticar no PostgreSQL ou em testes
locais; são segredos distintos.

## Limites desta etapa

- `ledger_balances` é uma projeção reconstruível; o journal é a fonte de
  verdade.
- O executor PAPER está conectado; ainda faltam recorder/replay operacional,
  persistência de fills e reconciliação de estado após crash.
- A migration 003 adiciona `lease_owner` para impedir que um worker finalize a
  mensagem de outro worker.
- A senha do PostgreSQL continua sendo local e separada das credenciais da
  Binance.
