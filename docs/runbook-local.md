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

Para validar o caminho real de inicialização do Worker com a migration atual e
o sink de auditoria operacional, use o smoke test abaixo. Ele cria um banco
efêmero, executa o host por alguns segundos em `PAPER/NO_TRADE` e não habilita
reconciliação, account stream, Binance ou qualquer endpoint mutável:

```bash
./scripts/test-worker-persistence-startup.sh
```

O processo é encerrado deliberadamente pelo harness após confirmar
conectividade e aplicação das migrations. O código de saída `124` do timeout é
esperado quando o host permanece saudável até o encerramento controlado.

Antes de qualquer probe autenticado, execute o preflight offline. Ele roda a
ferramenta em um diretório temporário sem `.env`, remove credenciais do
ambiente, exige `WORKER_NO_TRADE=true`/`PAPER` e comprova que o probe falha
antes de criar um cliente de rede:

```bash
./scripts/test-binance-readonly-preflight.sh
```

O script inicia um PostgreSQL local, executa migrations, valida journal/ledger,
lease, retry e idempotência, e encerra o container. Não colocar credenciais no
shell history, em logs ou no repositório.

Para executar o drill local dos controles independentes:

```bash
./scripts/test-operational-control.sh
```

O comando valida idempotência de `KillSwitch`, bloqueio de novas entradas em
`SafeMode`, reduções permitidas e reset autorizado. Ele não acessa exchange,
rede, credenciais ou endpoints de ordem; qualquer falha é fail-closed e gera
relatório redigido em `artifacts/operations/`.

Para habilitar uma reconciliação autenticada somente de leitura no startup,
configure as credenciais novas fora do Git e ative todas as guardas:

```bash
WORKER_RECONCILIATION_ENABLED=true \
WORKER_PERSISTENCE_ENABLED=true \
WORKER_NO_TRADE=true \
dotnet run --project src/CryptoTrading.Worker
```

O fluxo exige PostgreSQL local, consulta apenas account/balances/trades da
Testnet e não envia nem cancela ordens. Como o vínculo de trades externos ao
journal ainda é conservador, qualquer trade desconhecido ativa `SafeMode`. Cada
execução acrescenta somente um resumo JSONL redigido em
`WORKER_RECONCILIATION_REPORT_DIRECTORY` (por padrão `artifacts/reconciliation`);
`WORKER_RUNTIME_AUDIT_PATH` (por padrão `artifacts/runtime/runtime-transitions.jsonl`)
registra as transições do supervisor em JSONL encadeado por hash. O arquivo é
evidência operacional local, não contém credenciais nem autoriza resets.
balances, payloads e segredos não são gravados.
Sem credenciais, a configuração deve permanecer desabilitada; o startup não
faz chamadas autenticadas por padrão.
Os arquivos de evidência JSONL são gravados com modo Unix `0600`, além da
proteção do `.gitignore`; não altere essa permissão para compartilhar relatórios
sem uma cópia redigida e aprovada.

Para validar somente a fronteira autenticada, depois de revogar as chaves
expostas e criar uma chave Spot Testnet nova com permissões mínimas, use:

```bash
./scripts/check-testnet-secret-storage.sh
./scripts/test-binance-testnet-auth-readonly.sh
```

O script recusa ausência de credenciais, ambiente diferente de `TESTNET`, URL
fora do endpoint HTTPS exato da Testnet e `BINANCE_WITHDRAWALS_ENABLED` diferente
de `false`. Account, open orders e trades são os únicos recursos chamados. A
execução grava apenas um relatório redigido em `artifacts/binance/`; stdout e
stderr brutos não são copiados, mas uma única linha de diagnóstico com prefixo
controlado pode registrar etapa/status/razão segura. Na Spot Testnet,
`canWithdraw` é somente uma capability observada: a Testnet não expõe `/sapi`
e este adapter não possui endpoint de saque. Corpos, headers, assinaturas e
credenciais nunca entram no relatório. Ausência de um resultado privado bem-
sucedido mantém o gate em `No-Go`.

Para persistir alertas operacionais locais redigidos, defina
`WORKER_ALERT_FILE_DIRECTORY=artifacts/alerts`. Cada alerta será acrescentado
ao arquivo diário `alerts-YYYYMMDD.jsonl`; o valor vazio mantém o sink em
memória. O arquivo é criado com modo Unix `0600`. Esse canal não substitui
webhook/on-call externo.

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
