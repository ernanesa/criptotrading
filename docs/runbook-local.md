# Runbook operacional local

Este runbook cobre o modo local/Testnet. O sistema deve permanecer em `PAPER` ou
`NO_TRADE` por padrão. Nenhum procedimento deste documento autoriza envio de
ordens reais.

## Estados e resposta

| Sintoma | Ação imediata | Critério de retorno |
|---|---|---|
| `StrategyHealth=NoTrade` | preservar evidências; não forçar execução | dados frescos, RiskPolicy válida e reconciliação sem drift |
| `StrategyHealth=Blocked` | ativar/confirmar `SafeMode`; investigar reason code | revisão independente e reset autorizado |
| drift de saldo/fill | congelar novas entradas; manter apenas reduções aprovadas | reconciliação repetida sem divergência |
| timeout ambíguo | não reenviar cegamente; consultar `clientOrderId` | estado confirmado ou intervenção manual |
| banco indisponível | parar dispatcher; preservar outbox e logs | readiness saudável e migration compatível |

## Comandos de diagnóstico

```bash
export PATH="$PWD/.tools/dotnet:$HOME/.dotnet:$PATH"
dotnet build CryptoTrading.slnx --no-restore --configuration Release
dotnet test CryptoTrading.slnx --no-build --no-restore --configuration Release --verbosity minimal
./scripts/scan-secrets.sh
POSTGRES_PASSWORD='validation-only-not-a-credential' docker compose config --quiet
```

Para o teste de persistência, usar somente a senha efêmera do script:

```bash
./scripts/test-postgres-integration.sh
```

O script inicia um PostgreSQL local, executa migrations, valida journal/ledger,
lease, retry e idempotência, e encerra o container. Não colocar credenciais no
shell history, em logs ou no repositório.

## Evidência mínima de incidente

Registrar UTC, commit, versão do dataset, `correlationId`, reason code, estado
operacional, hash dos logs exportados e decisão Go/No-Go. Redigir chaves,
segredos, tokens, payloads privados e identificadores pessoais.

## Escalonamento

1. Operador preserva o estado e ativa SafeMode/KillSwitch quando houver risco.
2. Revisor de Risk/Execution confirma causa e evidência.
3. Somente após aprovação explícita pode ocorrer reset autorizado.
4. Qualquer integração privada continua bloqueada até revogação das chaves
   anteriormente expostas e geração de novas chaves Testnet com permissões
   mínimas.
