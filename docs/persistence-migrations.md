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
- `RecordKnownTradeAsync` registra de forma idempotente o `trade_id` externo
  somente após o evento correspondente estar no journal; a tabela
  `reconciliation_known_trades` é append-only e serve ao reader da
  reconciliação. IDs ausentes continuam sendo divergência fail-closed.
- `AppendJournalEntryWithKnownTradeAsync` deve ser usado quando o fill externo
  e o lançamento contábil forem confirmados juntos: journal, postings, projeção
  de saldo e `trade_id` são gravados na mesma transação serializável.
- `IRiskPolicyStore` registra documentos imutáveis na migration 006 e grava
  transições (`Draft`, `Approved`, `Active`, `Superseded`, `Revoked`) como
  eventos append-only. O hash SHA-256 cobre os valores canônicos da policy e
  sua janela de validade; aprovação exige ator diferente do criador.
- `GetActiveRiskPolicyAsync` retorna somente uma policy aprovada, ativa e dentro
  da janela UTC solicitada. Ausência de policy ativa retorna `null`; o Worker
  pode transformar isso em bloqueio de startup com
  `WORKER_RISK_POLICY_REQUIRED=true`.
- `IAccountStreamEventStore` grava eventos de conta em `account_stream_events`
  (migration 007), com `dedupe_key`, payload JSON canonicalizado, SHA-256,
  janela de leitura e trigger append-only. Replays idênticos são no-op; colisão
  de identidade com conteúdo diferente falha fechado.

Migration `008_account_stream_session_identity` acrescenta `session_id`,
`session_generation` e `subscription_id` ao envelope append-only. O cliente
moderno da Binance atribui uma identidade nova a cada conexão WebSocket e uma
geração monotônica ao reconnect; o Worker persiste esses valores junto de cada
evento e do marcador `connectionEstablished`. Assim é possível distinguir
duplicação, reconnect e encerramento remoto durante a reconciliação. Linhas
anteriores à migration permanecem legíveis com identidade nula e não são
preenchidas retroativamente.

Migration `009_reconciliation_open_orders` adiciona a projeção local mínima de
ordens abertas (`reconciliation_open_orders`). Ela não substitui o journal: é
uma projeção atualizável, alimentada pelo futuro OMS, usada apenas para comparar
`clientOrderId`, símbolo, `orderId` e status com o snapshot read-only da Binance.
Qualquer ordem externa desconhecida, ausente localmente ou com status divergente
entra em SafeMode.

Migration `031_reconciliation_events` adiciona a trilha append-only de eventos
de reconciliação. `IReconciliationEventStore` persiste o tipo/schema do evento,
correlação, instante observado, JSONB e SHA-256 do payload. Repetir o mesmo
`event_id` e conteúdo é idempotente; reutilizar a identidade com conteúdo ou
metadados diferentes falha fechado. O Worker seleciona o sink PostgreSQL quando
a persistência está habilitada; falha ao gravar evidência força SafeMode antes
de propagar a falha.

A projeção de ordens abertas aplica eventos do account stream por um upsert SQL
atômico, condicionado ao timestamp e à monotonicidade do preenchimento. Eventos
terminais deixam uma tombstone inativa com o estado final; um `NEW` ou
`PARTIALLY_FILLED` atrasado não pode reativar a mesma identidade após restart.
Uma leitura REST autoritativa não reativa uma linha inativa; ela só pode atualizar
uma identidade já ativa no caminho explícito de resolução. O teste de integração
`AccountStreamTerminalTombstoneRejectsDelayedActiveReportAcrossRestart` prova
esse comportamento com PostgreSQL real quando executado pelo harness local.

Migration `032_open_order_exchange_update_time` guarda o `updateTime` da exchange
separadamente do instante local de observação. A resolução REST de uma ordem
ambígua exige esse timestamp e só atualiza uma linha ainda ativa cuja versão não
seja mais nova. Projeções locais não reativam linhas inativas e uma tombstone
terminal permanece terminal; uma visão da exchange que contradiga isso gera
mismatch/SafeMode para investigação em vez de sobrescrever evidência.
Para account-stream, `updated_at` é o instante local de recebimento e
`exchange_updated_at` é o event-time da Binance; decisões de ordenação usam o
último e não comparam relógios locais e da exchange quando ambos estão
disponíveis. Relatórios ativos com event-time igual não regridem status sem um
avanço de quantidade executada; relatórios terminais prevalecem sobre ativos
com o mesmo timestamp.

Migration `010_operational_control_audit` adiciona a tabela append-only de
transições de `KillSwitch`/`SafeMode`. Cada linha preserva o schema versionado,
os modos anterior e seguinte, ator, motivo, chave de idempotência, eventual
aprovação e o SHA-256 do payload canônico. Repetições do mesmo evento ou chave
de idempotência são no-op somente quando todos os campos coincidem; colisões de
identidade falham fechado. Um trigger PostgreSQL impede `UPDATE` e `DELETE`.
O contrato de armazenamento é assíncrono (`IOperationalControlAuditStore`) e o
Worker usa `IAsyncOperationalControlAuditSink`: o append PostgreSQL é aguardado
antes de publicar o novo snapshot. Assim, o caminho hospedado não bloqueia uma
thread com `GetAwaiter().GetResult()` e qualquer falha/cancelamento mantém a
transição fail-closed. `PostgresOperationalControlAuditSink` ainda implementa
`IOperationalControlAuditSink` somente como compatibilidade para consumidores
síncronos legados; esse caminho permanece bloqueante por contrato e não deve ser
usado em hosted services.

O teste de integração `ReplaysOpenOrderProjectionAcrossStoreRestartAndRetainsRemovalState`
valida o contrato operacional dessa projeção: upsert repetido é idempotente,
uma nova instância do store lê apenas a identidade/status ativos mais recentes,
remoção persiste após restart e uma observação posterior pode recriar a projeção.
O teste usa um `clientOrderId` isolado por execução e não se conecta à Binance;
ele prova somente replay/restart local do PostgreSQL, não reconciliação autenticada.

O Worker também registra esse sink de forma explícita: `WORKER_PERSISTENCE_ENABLED=false`
(padrão) mantém `IOperationalControlAuditSink` in-memory; somente
`WORKER_PERSISTENCE_ENABLED=true` seleciona `PostgresOperationalControlAuditSink`.
O `IOperationalControl` é registrado uma única vez para evitar que as branches de
reconciliação ou account stream substituam o sink. Com persistência habilitada,
uma falha no PostgreSQL faz a transição falhar fechado antes de alterar o estado
em memória; a leitura de `Snapshot` continua não bloqueante enquanto o append
assíncrono está em andamento. `WORKER_APPLY_MIGRATIONS=true` deve ser usado para preparar a
migration 010 antes de habilitar transições operacionais persistidas.

## Ativação pelo Worker

O Worker registra o `PostgresTradingStore` somente quando
`WORKER_PERSISTENCE_ENABLED=true`. As migrations também exigem a flag explícita
`WORKER_APPLY_MIGRATIONS=true`; portanto, iniciar o processo com defaults não
abre conexão nem altera o banco. A connection string deve ser fornecida por
`POSTGRES_CONNECTION_STRING` em ambiente local e nunca deve ser commitada.
Com persistência habilitada, o Worker também registra
`PostgresReconciliationEventSink`; sem persistência, reconciliation habilitada
falha na composição em vez de descartar silenciosamente eventos de auditoria.

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
- O executor PAPER e o recorder/replay determinístico estão conectados em suas
  respectivas camadas; a persistência durável de fills de exchange e a
  reconciliação completa de estado financeiro após crash continuam pendentes.
- A migration 003 adiciona `lease_owner` para impedir que um worker finalize a
  mensagem de outro worker.
- A migration 005 adiciona a evidência append-only de trade IDs conhecidos para
  reconciliação; ela não autoriza submit/cancel e o método atômico deve ser
  preferido para criar o vínculo durável ao journal.
- A migration 006 adiciona o registry de `RiskPolicy`; seu `.down.sql` é
  destrutivo para evidências de aprovação e exige exportação prévia.
- A migration 007 adiciona evidência durável de eventos do User Data Stream;
  seu rollback também exige exportação prévia.
- A migration 011 adiciona o schema append-only de governança experimental:
  preregistration, trials, auditoria de acesso ao holdout e eventos de
  contaminação. Os quatro conjuntos são versionados por hash e protegidos por
  triggers contra `UPDATE`/`DELETE`; a implementação do adapter transacional
  deve preservar a idempotência dos contratos de `CryptoTrading.Domain.Research`.
- O snapshot autenticado pagina trades por `fromId`, com limite configurável e
  falha fechada quando a paginação não termina; IDs repetidos entre páginas são
  deduplicados antes da reconciliação.
- A senha do PostgreSQL continua sendo local e separada das credenciais da
  Binance.
