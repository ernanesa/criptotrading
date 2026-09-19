# PostgreSQL local

O repositório fornece uma instância PostgreSQL local para desenvolvimento e
paper trading. O serviço não usa credenciais do Binance e não deve ser
exposto além do host local.

## Pré-requisitos

- Docker Engine e Docker Compose;
- uma senha temporária definida apenas no shell atual.

Não grave a senha no Git, em scripts, no histórico de tickets ou em mensagens.
Para uma sessão local, gere uma senha aleatória e exporte-a:

```bash
export POSTGRES_PASSWORD="$(openssl rand -hex 32)"
```

Também é possível definir `POSTGRES_DB`, `POSTGRES_USER` e `POSTGRES_PORT` se
esses defaults precisarem ser alterados. O Compose usa `cryptotrading`,
`cryptotrading` e `5432` por padrão.

## Subir e validar

```bash
docker compose up -d postgres
docker compose ps postgres
docker compose exec postgres pg_isready -U "${POSTGRES_USER:-cryptotrading}" -d "${POSTGRES_DB:-cryptotrading}"
```

O estado esperado é `healthy`. O volume nomeado
`cryptotrading-postgres-data` persiste os dados entre reinícios. A porta é
publicada somente em `127.0.0.1`.

Para parar o serviço preservando os dados:

```bash
docker compose stop postgres
```

Para remover o container sem apagar o banco:

```bash
docker compose down
```

Não use `docker compose down -v` sem confirmar previamente: essa opção remove
o volume local e todos os dados armazenados nele.

## Contrato de conexão

O contrato esperado pelos próximos componentes é:

```text
Host=localhost;Port=5432;Database=cryptotrading;Username=cryptotrading;Password=<valor local>
```

Migrations, journal, ledger, outbox/inbox e readiness real ainda são fatias
posteriores. Até que sejam implementados, o health endpoint da aplicação não
é evidência de conectividade com PostgreSQL.

## Evidência mínima

Antes de marcar a tarefa de Compose como concluída, registrar:

```bash
docker compose config
docker compose up -d postgres
docker compose ps postgres
```

Não cole a saída caso ela contenha variáveis sensíveis. A senha deve continuar
fora dos arquivos versionados.
