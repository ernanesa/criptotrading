# Local secret storage policy

This directory is intentionally ignored by Git. Do not place credentials in
the repository, ClickUp exports, logs, shell history, Dockerfiles, images,
fixtures, screenshots or CI variables.

## Compose isolation contract

The base `compose.yaml` is PAPER/NO_TRADE and never passes Binance API keys,
secrets, or credential-file paths to a container. Start it on its own for
normal local work.

The tracked `compose.testnet.yaml` is an explicit, future Testnet opt-in. It
bind-mounts one private directory into the Worker and passes only the two
in-container file paths. It does not enable Testnet execution, orders,
reconciliation, account streaming, autonomy, or withdrawals. Use it only
after the independent Testnet gates have been approved:

```bash
cp .env.testnet.example .env.testnet
# Set BINANCE_TESTNET_SECRETS_DIRECTORY to the private directory created below.
docker compose --env-file .env.testnet -f compose.yaml -f compose.testnet.yaml --profile worker up
```

`.env.testnet` is ignored by Git and contains a directory path only; never put
credential values, `BINANCE_API_KEY`, or `BINANCE_API_SECRET` in it. For a
direct host-only command, the separate ignored `.env` may contain only the
absolute `BINANCE_API_KEY_FILE` and `BINANCE_API_SECRET_FILE` paths. The base
Compose file does not consume either path.

## Stronger alternative: external secret files

Use an external directory owned by the local user, for example:

```text
/home/ernane/.config/cryptotrading/testnet/binance_api_key
/home/ernane/.config/cryptotrading/testnet/binance_api_secret
```

The directory must be mode `700` and each file mode `600`. The application
receives file paths, not secret values, and fails closed when the path,
environment, endpoint or permissions do not match. The Compose override
bind-mounts the entire private directory because the runtime verifies both the
file and its parent permissions.

O runtime implementa `BINANCE_API_KEY_FILE` e `BINANCE_API_SECRET_FILE`. Os
caminhos precisam ser absolutos; em Linux/macOS, o arquivo não pode ter bits de
leitura/escrita/execução para grupo ou outros, e o diretório pai também deve
ser privado. Se um valor direto `BINANCE_API_KEY`/`BINANCE_API_SECRET` estiver
presente, ele tem precedência para manter compatibilidade com o contrato `.env`.
Valores de arquivo contendo whitespace, arquivos inexistentes ou permissões
inseguras são tratados como credenciais ausentes e falham na validação.
Essa mesma resolução é usada pelo cliente REST autenticado e pelo User Data
Stream; não há um segundo caminho que leia chaves diretamente da configuração.

Create the files with the repository helper:

```bash
./scripts/init-testnet-secrets.sh
```

The helper reads values without echoing them, never prints their contents and
does not accept values as command-line arguments. Do not run it with values
embedded in shell history.

## Testnet file contract

```text
BINANCE_TESTNET_SECRETS_DIRECTORY=/home/ernane/.config/cryptotrading/testnet
```

The Testnet key must be separate from any future production key. Withdrawals,
transfers, Margin and Futures remain disabled. A secret that was posted in chat
is not acceptable evidence or a valid runtime credential; revoke and rotate it.

## Validation checklist

- [ ] key was created on `testnet.binance.vision`;
- [ ] old/exposed key was revoked;
- [ ] new key was entered locally, not in chat;
- [ ] directory is `700`, files are `600`;
- [ ] `TRADING_ENVIRONMENT` and endpoint are both `TESTNET`;
- [ ] no withdrawal/transfer permission exists;
- [ ] secret scan passes;
- [ ] logs and error output contain no key or secret;
- [ ] authenticated integration is tested only after the public boundary and
      fail-closed configuration checks pass.
