# Local secret storage policy

This directory is intentionally ignored by Git. Do not place credentials in
the repository, ClickUp exports, logs, shell history, Dockerfiles, images,
fixtures, screenshots or CI variables.

## Selected local `.env` contract

For the initial local Testnet setup, use the ignored repository file `.env`:

```text
BINANCE_API_KEY=<new-testnet-key>
BINANCE_API_SECRET=<new-testnet-secret>
```

Create it from the tracked template with `cp .env.example .env`, fill the two
values locally, and keep its permissions at `600`. Never commit or send the
populated file. The repository `.gitignore` ignores `.env` and `.env.*`.

## Stronger alternative: external secret files

Use an external directory owned by the local user, for example:

```text
/home/ernane/.config/cryptotrading/testnet/binance_api_key
/home/ernane/.config/cryptotrading/testnet/binance_api_secret
```

The directory must be mode `700` and each file mode `600`. The application
should receive file paths, not secret values, and fail closed when the path,
environment, endpoint or permissions do not match.

Create the files with the repository helper:

```bash
./scripts/init-testnet-secrets.sh
```

The helper reads values without echoing them, never prints their contents and
does not accept values as command-line arguments. Do not run it with values
embedded in shell history.

## Environment contract

```text
TRADING_ENVIRONMENT=TESTNET
BINANCE_REST_BASE_URL=https://testnet.binance.vision/api
BINANCE_WS_BASE_URL=wss://testnet.binance.vision/ws
BINANCE_API_KEY_FILE=/home/ernane/.config/cryptotrading/testnet/binance_api_key
BINANCE_API_SECRET_FILE=/home/ernane/.config/cryptotrading/testnet/binance_api_secret
BINANCE_WITHDRAWALS_ENABLED=false
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
