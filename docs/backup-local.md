# Backup local agendável do PostgreSQL

`scripts/backup-postgres-scheduled.sh` é um invólucro local para
`backup-postgres.sh`. Cada tentativa continua criando um dump lógico PostgreSQL
em formato custom, verifica-o com `pg_restore --list`, calcula SHA-256 e só
publica o diretório de artefato depois da verificação. O invólucro adiciona
lock não bloqueante, timeout por tentativa, retry limitado e um evento JSON
sanitizado em `stderr`.

Ele não escolhe nem instala cron, systemd ou qualquer outro scheduler. O
operador decide a ferramenta e a janela de agendamento fora deste repositório.
Também não restaura dados, não altera Docker, não contata Binance e não muda os
invariantes PAPER/NO_TRADE.

## Pré-requisitos e diretório

Escolha previamente um diretório local existente, privado (`0700`), de
propriedade do usuário atual e fora de qualquer worktree Git. O script recusa
links simbólicos, a raiz do filesystem, o repositório e diretórios com acesso
por grupo ou outros. Crie-o uma única vez, fora do repositório:

```bash
install -d -m 700 "$HOME/cryptotrading-backups"
```

O processo precisa de `bash`, `flock`, `timeout`, `docker`, `pg_dump` e
`pg_restore` no container PostgreSQL. O backup usa o identificador do
container, banco e usuário recebidos na linha de comando; não lê `.env` e não
imprime esses valores, o dump ou a saída do Docker.

## Contrato de execução

Todos os parâmetros abaixo são obrigatórios. `retry-count` é o número de novas
tentativas após a primeira e é limitado a 10. `attempt-timeout-seconds` é
limitado a 24 horas. O lock deve ter exatamente o nome
`.backup-postgres-scheduled.lock` no diretório de saída; ele é um arquivo
privado `0600` e uma execução concorrente falha de imediato.

```bash
bash scripts/backup-postgres-scheduled.sh \
  --container <container-local> \
  --database <database-local> \
  --user <usuario-local> \
  --output-directory "$HOME/cryptotrading-backups" \
  --min-free-bytes 10737418240 \
  --lock-file "$HOME/cryptotrading-backups/.backup-postgres-scheduled.lock" \
  --attempt-timeout-seconds 900 \
  --retry-count 2 \
  --retry-delay-seconds 30 \
  --encryption-policy local-private-unencrypted-approved \
  --retention-policy no-automatic-deletion-approved
```

O evento final JSON contém somente timestamp, status, identificador de execução,
tentativa, máximo de tentativas, duração e uma razão enumerada. Valores de
container, banco, usuário, diretório e saída do processo filho não aparecem no
evento. Os estados são `succeeded`, `retrying` e `failed`; as razões possíveis
são `none`, `backup_command_failed`, `attempt_timed_out` e `lock_unavailable`.
Os logs detalhados do filho ficam em arquivo temporário privado e são removidos
ao encerrar a rotina.

## Decisões humanas obrigatórias

O backup é plaintext dentro de um diretório local privado. Esta rotina não
implementa criptografia e não aceita chaves. A opção
`--encryption-policy local-private-unencrypted-approved` é uma confirmação
explícita do responsável de que esta política local foi aprovada; ausência ou
outro valor falha fechado. Para criptografia em repouso, gestão/rotação de
chaves ou cópia offsite, defina e aprove uma política operacional separada e
adicione uma implementação revisada antes de agendar backups.

Não existe retenção automática: nenhum artefato é removido por esta rotina. A
opção `--retention-policy no-automatic-deletion-approved` confirma essa decisão
humana; ausência ou outro valor falha fechado. Não introduza exclusão de dumps
sem uma política de retenção aprovada, requisitos de RPO/RTO e revisão de
restauração.

O comando não substitui testes de restore. Antes de tratar o mecanismo como
operacional, execute e retenha evidência de um drill autorizado e isolado com
`bash scripts/test-postgres-backup-restore.sh`; esse drill não restaura o volume
da aplicação. Escolha o scheduler, frequência, alertas, destino offsite,
criptografia, retenção e procedimento de restauração somente por decisão humana
registrada.

## Contrato offline

O teste de contrato não acessa Docker real, rede, PostgreSQL, cron ou systemd:

```bash
bash scripts/test-backup-postgres-scheduled.sh
```

Ele simula o Docker para verificar publicação privada, retry limitado, timeout,
contenção de lock, acknowledgement obrigatório de política e ausência de dados
de conexão nos eventos JSON. Isso não é evidência de backup, criptografia,
retenção ou restore operacional real.
