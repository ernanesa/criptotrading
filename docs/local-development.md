# Desenvolvimento local

## Pré-requisitos atuais

- SDK .NET 11 RC pinado em `global.json` e instalado localmente em
  `.tools/dotnet/`;
- Node 26 disponível;
- pnpm ainda precisa ser habilitado/pinado antes do shell React;
- Docker Engine e Docker Compose disponíveis;
- PostgreSQL/observabilidade serão adicionados no Compose em uma etapa seguinte.

Use o SDK pinado sem alterar o SDK global:

```bash
export DOTNET_ROOT="$PWD/.tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --info
dotnet restore CryptoTrading.slnx --locked-mode
dotnet build CryptoTrading.slnx --no-restore
dotnet test CryptoTrading.slnx --no-restore
```

O SDK instalado é `11.0.100-rc.1.26425.128`; o build registra a mensagem de
preview esperada. A atualização para GA deve ser um commit isolado que atualize
`global.json`, valide dependências e remova a condição preview.

## Smoke checks sem credenciais

```bash
./scripts/test-binance-testnet-public.sh
```

Esse teste cobre somente a fronteira pública (`ping`, `time` e
`exchangeInfo`). Não é evidência de autenticação ou de envio de ordens.

## Secrets Testnet

Revogue qualquer chave exposta e gere uma nova na Spot Testnet. Depois execute:

```bash
./scripts/init-testnet-secrets.sh
```

O helper grava somente em `/home/ernane/.config/cryptotrading/testnet/`, com
diretório `700` e arquivos `600`. Nunca passe os valores como argumentos e
nunca os cole em commits, logs, tickets, screenshots ou mensagens.

O teste privado só será habilitado quando o adapter Binance e os contratos de
ambiente estiverem implementados. Ele deve falhar fechado se o endpoint não
for Testnet, se o ambiente não for `TESTNET`, se withdrawals estiverem ativos,
ou se o segredo não tiver permissões de arquivo mínimas.

## Próximas fatias

1. adicionar `TimeProvider`, IDs e módulos Domain;
2. adicionar journal, ledger e outbox/inbox;
3. adicionar PostgreSQL/Compose e health/readiness reais;
4. implementar adapter read-only da Binance;
5. implementar OMS, reconciliação e RiskPolicy;
6. somente então testar account/order na Testnet com a nova chave local.
