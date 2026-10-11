# Promotion Readiness Matrix PRM-20260919-foundation

**Candidato:** fundação local CryptoTrading / baseline Trend Following BTC/USDT
**De → para:** implementação local → Paper (somente simulação)
**Owner/revisor/aprovador:** Ernane (Operator & Risk Owner) / pendente / pendente (Gate M3)
**Decisão:** **No-Go para Shadow, Canary e Live; Paper local permitido para desenvolvimento (Live status estritamente NO-GO até signoff humano M3)**
**Parâmetros aprovados pelo operador Ernane:** Planejamento de R$ 100,00; conversão USDTBRL <= 60s com haircut de 2%; hard cap de 20,00 USDT; 1 posição Spot; perda diária máxima R$ 2,00; drawdown máximo R$ 5,00; risco máximo por trade R$ 1,00; reserva de taxas 10 bps + 25 bps; backup operacional AES-256 a cada 15m (retenção 7d, fail-closed se disco livre < 2 GB ou < 10%).
**Válida até:** 2026-09-19 (snapshot; expira ao mudar código, configuração, RiskPolicy ou dados)
**Commit de referência:** `29291c1`

## Matriz de evidências

| Gate | Resultado observado | Evidência | Status |
|---|---|---|---|
| Build reprodutível | `dotnet build ... Release`: 0 warnings, 0 errors | `RELEASE-20260919-foundation.md` | PASS |
| Testes unitários/contratuais | Suite da solução verde; projetos Domain, Exchange, Backtesting, Persistence, Observability, Configuration, Worker e Reconciliation | comando registrado no release evidence | PASS |
| Formatação | `dotnet format ... --verify-no-changes` verde | release evidence | PASS |
| Scan de segredos | `./scripts/scan-secrets.sh` verde; nenhum segredo versionado | release evidence; `.env` permanece local/ignorado | PASS |
| PostgreSQL/migrations/outbox | integração dedicada Compose verde: migration, ledger, lease/retry/idempotência | `scripts/test-postgres-integration.sh`, commit `f7b073a` | PASS local |
| RiskPolicy/kill switch/SafeMode | contratos e testes determinísticos presentes | `src/CryptoTrading.Domain/Risk`, commit `e080792` | PASS de código; drill pendente |
| Market data/order book/recorder | APIs públicas, order book bounded e recorder/replay determinístico testados | commits `9217d4f`, `83657bd` | PASS de código; dataset OOS pendente |
| Strategy Health | estados `Healthy`/`NoTrade`/`Blocked` e razões auditáveis testados | `src/CryptoTrading.Domain/Risk/StrategyHealth.cs`, `8ffb4d8` | PASS de código |
| Edge estatístico após custos | nenhum resultado OOS/walk-forward aprovado | `plan/10-research-validation.md` | **PENDENTE — bloqueia promoção** |
| Amostra, regimes, drawdown e cauda | thresholds numéricos aprovados inexistentes | RiskPolicy/PRM ainda sem valores aprovados | **PENDENTE** |
| Paper/shadow divergence e soak | nenhum período/volume de soak fechado | checklist de implementação | **PENDENTE** |
| Fill, slippage, latência e paridade | simulador existe; validação empírica contra Testnet pendente | checklist; sem ordem enviada | **PENDENTE** |
| Reconciliação de conta/orders/fills | serviço read-only e drift fail-closed implementados; execução contra conta não realizada | `src/CryptoTrading.Reconciliation`, `eb12678` | **PENDENTE operacional** |
| Restore/restart/crash recovery | integração de persistência existe; drill completo e RPO/RTO não evidenciados | `docs/recovery-drill.md` | **PENDENTE** |
| Alertas/runbooks/threat model/backup/chaos | documentação parcial; drills e alert delivery não comprovados | `docs/runbook-local.md`, `docs/threat-model-local.md` | **PENDENTE** |
| Credenciais e permissões Testnet | nenhuma chave foi usada neste snapshot; segregação/read-only ainda deve ser comprovada com novas credenciais | checklist | **PENDENTE — bloqueia Testnet** |

## Artefatos obrigatórios

- [x] Strategy Spec, RiskPolicy contratual e Dataset/Experiment templates.
- [ ] RiskPolicy preenchida com valores, unidades, janelas, validade e aprovador.
- [ ] Threat Model e Reconciliation Report de uma execução real Testnet.
- [ ] CanaryPlan e ReleaseEvidence aprovados por responsável humano.
- [ ] Restore, kill-switch, SafeMode e reconciliação executados e anexados.
- [ ] Nenhuma vulnerabilidade crítica/alta ou incidente P0/P1 aberto.

## Condições de rejeição

Qualquer evidência ausente/expirada, divergência financeira não explicada, segredo exposto, falha de reconciliação, stale data, falta de operador, threshold sem unidade ou tentativa de ordem fora do Paper mantém a decisão em `No-Go`.
