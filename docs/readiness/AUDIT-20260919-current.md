# Readiness audit — estado corrente

**Data (UTC):** 2026-09-19  
**Commit de código auditado:** `410790a`  
**Escopo:** fundação local, Paper/NO_TRADE e integração Binance read-only  
**Decisão:** **No-Go para Shadow/Canary/Live; Paper local permitido**

## Evidências executadas

Os comandos abaixo foram executados contra o commit de código indicado acima.
O SDK foi chamado pelo caminho local fixado porque o `dotnet` global disponível
na sessão não resolvia o SDK preview declarado em `global.json`.

| Gate | Comando | Resultado |
|---|---|---|
| Testes da solução | `./.tools/dotnet/dotnet test CryptoTrading.slnx --configuration Release --verbosity minimal --no-restore` | PASS — 169 aprovados, 0 falhas, 0 skips |
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

## Gates que continuam pendentes

- dataset de mercado real com OOS/walk-forward/sensitivity e critérios
  quantitativos aprovados;
- soak Shadow com período, volume, regimes, recovery e intervention-rate
  medidos;
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
