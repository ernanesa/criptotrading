# Readiness audit — estado corrente

**Data (UTC):** 2026-09-19  
**Commit de código auditado:** `a5b3f62`
**Escopo:** fundação local, Paper/NO_TRADE e integração Binance read-only  
**Decisão:** **No-Go para Shadow/Canary/Live; Paper local permitido**

## Evidências executadas

Os comandos abaixo foram executados contra o commit de código indicado acima.
O SDK foi chamado pelo caminho local fixado porque o `dotnet` global disponível
na sessão não resolvia o SDK preview declarado em `global.json`.

| Gate | Comando | Resultado |
|---|---|---|
| Testes da solução | `./.tools/dotnet/dotnet test CryptoTrading.slnx --configuration Release --verbosity minimal --no-restore` | PASS — 204 aprovados, 0 falhas, 0 skips |
| Integração PostgreSQL | `./scripts/test-postgres-integration.sh` | PASS — 4 testes, incluindo vínculo atômico journal/ledger/trade, migration 005, persistência idempotente e rejeição de símbolo conflitante |
| Paper chaos/soak | `./scripts/test-paper-soak-chaos.sh` | PASS — 1 teste, 64 ordens simuladas, sem side effect duplicado |
| Backup/restore isolado | `./scripts/test-postgres-backup-restore.sh` | PASS — dump restaurado em banco efêmero; recursos removidos |
| Secret scan | `./scripts/scan-secrets.sh` | PASS — nenhum padrão de credencial em arquivos versionados |
| Compose | `POSTGRES_PASSWORD='validation-only-not-a-credential' docker compose config --quiet` | PASS |
| Integridade documental | `git diff --check` | PASS |

## O que estes gates comprovam

- OOS sintético, walk-forward determinístico, property tests financeiros,
  simulador Paper, Monte Carlo/stress e controles de risco estão cobertos pela
  suite automatizada.
- Crash recovery de leases e idempotência PostgreSQL são cobertos pelo teste de
  integração dedicado; o backup/restore isolado foi executado nesta auditoria.
- O Paper chaos/soak foi executado sem rede, PostgreSQL, credenciais ou ordens.
- O probe autenticado permanece read-only e falha fechado sem credenciais.
- O avaliador local `ShadowDriftEvaluator` calcula divergência, stale e recovery
  de observações redigidas sem qualquer capacidade de submissão de ordem.
- O soak determinístico local foi executado com 64 observações, divergência
  sintética, janelas stale e recovery; o runner repetiu três janelas, agregou os
  resumos e falhou fechado para credenciais presentes. O relatório está em
  `artifacts/shadow/` e não substitui o Shadow operacional com dados reais.
- O Worker Shadow local persistiu relatório JSONL redigido e publicou alertas
  deduplicados de divergência, stale e recovery; canais externos ainda não
  foram configurados.
- O Worker possui uma reconciliação autenticada read-only opt-in no startup,
  condicionada a PostgreSQL local e `NO_TRADE`; sem credenciais novas ela falha
  antes de qualquer chamada. A execução contra a conta Testnet ainda não foi
  realizada.
- A migration 005 persiste IDs externos conhecidos de forma append-only e o
  Worker os lê do PostgreSQL para a reconciliação; conflitos de símbolo para o
  mesmo ID falham de forma explícita.
- A fronteira autenticada rejeita contas com withdrawals habilitados ou
  permissões fora da allowlist read-only; a coleta de trades pagina por
  `fromId`, deduplica IDs e falha fechado ao atingir o limite configurado.
- A reconciliação compara a união dos ativos locais e externos; um ativo
  presente apenas em um dos lados gera divergência e entrada em `SafeMode`.
- O vínculo entre evento contábil e `trade_id` externo possui uma operação
  transacional única, com replay idempotente e conflito de símbolo fail-closed.

## Gates que continuam pendentes

- dataset de mercado real com OOS/walk-forward/sensitivity e critérios
  quantitativos aprovados;
- soak Shadow com período, volume, regimes, recovery e intervention-rate
  medidos em dados reais;
- execução autenticada read-only contra uma nova chave Testnet, com relatório
  redigido de balances, open orders e trades e reconciliação do ledger;
- alert delivery real, RPO/RTO medidos e reconstrução completa de estado;
- RiskPolicy, PRM e CanaryPlan preenchidos com valores/unidades, validade e
  aprovação humana;
- qualquer submissão de ordem, mesmo em Testnet, continua proibida até decisão
  explícita e independente.

## Regra de interpretação

`PASS` nesta auditoria significa somente que o gate local indicado foi
executado. Não converte evidência sintética em evidência de mercado, não prova
paridade com a Binance e não autoriza promoção nem capital.
