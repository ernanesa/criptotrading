# Persistência local e migrations

O projeto `CryptoTrading.Persistence` entrega o contrato SQL versionado para a
primeira fatia durável: journal append-only, postings do ledger, projeção de
saldos e inbox/outbox idempotentes. Os scripts são embarcados no assembly para
que um runner futuro possa executá-los; nenhum acesso ao banco é feito em
runtime nesta etapa.

## Aplicação

Use uma conexão PostgreSQL local e aplique somente os arquivos `.up.sql`, em
ordem lexicográfica, dentro de uma transação por migration. A senha deve vir
do ambiente local e nunca deve ser colocada no comando, no Git ou em tickets:

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

## Limites desta etapa

- Não há `Npgsql`, repository ou conexão de produção ainda.
- `ledger_balances` é uma projeção reconstruível; o journal é a fonte de
  verdade.
- O outbox/inbox tem o contrato de lease, retry e deduplicação, mas o dispatcher
  será implementado em uma etapa posterior.
- A senha do PostgreSQL continua sendo local e separada das credenciais da
  Binance.
