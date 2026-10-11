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
# Modo com criptografia AES-256 e retenção de 7 dias aprovada:
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
  --encryption-policy aes256-keyfile \
  --encryption-key-file "$HOME/.secrets/backup.key" \
  --retention-policy prune-after-verified-7d

# Modo unencrypted legível apenas localmente (legado/dev):
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

O evento final JSON contém timestamp, status, identificador de execução, tentativa,
máximo de tentativas, duração, metas de `rpoTargetSeconds: 900` (15m) e
`rtoTargetSeconds: 600` (10m), políticas ativas e razão enumerada. Valores de
container, banco, usuário, diretório e chaves não aparecem no evento. Os estados são
`succeeded`, `retrying` e `failed`; as razões possíveis são `none`,
`backup_command_failed`, `attempt_timed_out` e `lock_unavailable`.

## Políticas de criptografia e retenção

### Criptografia AES-256 (`aes256-keyfile`)
Quando `--encryption-policy aes256-keyfile` é configurado:
- `--encryption-key-file <caminho>` é obrigatório e deve apontar para arquivo regular não-simbólico, com permissões estritas `0600`, cujo diretório pai tenha modo `0700` e pertença ao `$UID` do usuário executor.
- O backup lógico custom do PostgreSQL é primeiro verificado com `pg_restore --list`.
- Em seguida, é criptografado com `openssl enc -aes-256-cbc -pbkdf2 -salt -pass "file:$encryption_key_file"`.
- O dump em texto claro é removido imediatamente após a geração do artefato criptografado `backup.dump.enc`.
- O manifesto `manifest.json` registra `encryptedSha256`, `dumpSha256` e `encryptionAlgorithm: "AES-256-CBC-PBKDF2"`.
- Para descriptografar durante um restore:
  ```bash
  openssl enc -d -aes-256-cbc -pbkdf2 -salt -pass "file:<keyfile>" -in backup.dump.enc -out backup.dump
  ```

A opção alternativa `--encryption-policy local-private-unencrypted-approved` mantém o dump em formato custom unencrypted em diretório privado `0700`.

### Retenção (`prune-after-verified-7d`)
Quando `--retention-policy prune-after-verified-7d` é utilizado:
- A remoção de artefatos antigos ocorre **apenas após** a publicação e verificação com sucesso do novo backup.
- Diretórios de backup (`postgres-*`) no diretório de saída com idade superior a `${BACKUP_RETENTION_DAYS:-7}` dias são removidos.
- A opção alternativa `--retention-policy no-automatic-deletion-approved` mantém todos os artefatos sem exclusão automática.

O comando não substitui testes de restore. Execute periodicamente o drill isolado:
```bash
bash scripts/test-postgres-backup-restore.sh
```
O drill valida criptografia, integridade, detecção de corrupção, restauração determinística e asserta RTO <= 600s.

## Contrato offline

O teste de contrato não acessa Docker real, rede, PostgreSQL, cron ou systemd:

```bash
bash scripts/test-backup-postgres-scheduled.sh
```

Ele simula o Docker para verificar publicação privada, retry limitado, timeout,
contenção de lock, acknowledgement obrigatório de política e ausência de dados
de conexão nos eventos JSON. Isso não é evidência de backup, criptografia,
retenção ou restore operacional real.
