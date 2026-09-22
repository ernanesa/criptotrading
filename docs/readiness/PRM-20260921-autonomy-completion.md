# Promotion Readiness Matrix PRM-20260921-autonomy-completion

**Candidato:** Epic E-19 Autonomy Completion / Baseline Trend Following BTC/USDT (v1.0.0)
**De → para:** Backtest / Paper → Shadow (com capability de ordens desativada)
**Owner/revisor/aprovador:** Autonomous Cycle Agent / Codex / Revisor Humano (Pendente)
**Decisão:** **Go para Paper/Shadow local (read-only); No-Go estrito para Testnet e Live**
**Válida até:** 2026-09-30 (snapshot; expira caso haja alteração de código, contratos de risco ou migrações)
**Commit de referência:** `efc6826`

## Matriz de evidências

| Gate | Requisito / Threshold com unidade | Resultado observado / CI | Evidência | Status |
|---|---|---|---|---|
| Compilação & Build | Zero warnings, zero errors em Release | 0 avisos, 0 erros | `dotnet build --no-incremental` | PASS |
| Testes Unitários & Contratuais | 100% de testes automatizados passando | 510 testes PASS | `dotnet test` (todas as suites) | PASS |
| Scan de Segredos | Zero credenciais, chaves ou tokens versionados | 0 segredos detectados | `./scripts/scan-secrets.sh` | PASS |
| Validação de Rastreabilidade | Schema v2, sem referências órfãs ou IDs duplicados | 126 itens mapeados, sem órfãos | `./scripts/validate-traceability.sh` | PASS |
| Guard de Klines Fechados (US-190) | UTC strictly monotonic, staleness <= MaxMarketDataAge, sem gaps | Validado em envelopes fechados | `AutonomyCycleMarketDataTests.cs` | PASS |
| Snapshot de Features (US-191) | SHA-256 canônico, imutabilidade temporal | Decision não avaliada antes de AvailableAt | `BaselineFeatureSnapshotTests.cs` | PASS |
| Commit Atômico de Decisão (US-192) | Unicidade de identidade natural, outbox atômico em DISPATCHABLE | Isolamento serializável comprovado | `PostgresTradingStoreIntegrationTests.cs` | PASS |
| Startup Gates & RiskPolicy (US-193) | WithdrawalsEnabled == false; AutonomySymbol válido; validade checada | Falha fechada no boot em violações | `RiskPolicyStartupHostedServiceTests.cs` | PASS |
| Bloqueio por Ordens UNKNOWN (US-193) | Nenhuma nova exposição se houver ordem UNKNOWN | Status BLOCKED emitido imediatamente | `AutonomyCycleExecutionGuardsTests.cs` | PASS |
| TTL & Cancelamento Passivo (US-194) | Expiração determinística de tentativas | Cancelamento por store durável verificado | `ExpiredOrderCancellationTests.cs` | PASS |
| Terminal Resolution & Replan (US-195) | Cancel terminal gera replan candidate idempotente | Candidatura persistida para residual | `TestnetCancelOutboxMessageExecutorTests.cs` | PASS |
| Consumo de Replan no Ciclo (US-196) | Replan context injetado no trace do ciclo | CandidateId e Attempt rastreados | `AutonomyCycleHostedService.cs` | PASS |
| Classificador de Falhas (US-197) | Mapeamento puro de exceções em SafeMode / KillSwitch | Pure domain logic sem side effects | `FailureClassifierTests.cs` | PASS |
| Reconstrução de Decisão (US-198) | Reconstrução read-only com verificação de SHA-256 canônico | Audit report gerado por CLI / service | `AutonomyDecisionReconstructionTests.cs` | PASS |
| Conectividade Testnet com Chaves Reais | Credenciais segregated com canWithdraw=false | Chaves de teste ainda não fornecidas | Checklist de governança | **BLOQUEADO — Aguarda Aprovação** |

## Artefatos obrigatórios

- [x] Strategy Spec (`docs/trend-following-baseline.md`).
- [x] Contratos e schemas duráveis de auditoria (`src/CryptoTrading.Persistence/`).
- [x] Ferramenta de auditoria e replay sem efeitos colaterais (`tools/CryptoTrading.AutonomyDecisionReconstructor/`).
- [x] Runbook com comandos de diagnóstico, recuperação e rollback (`docs/runbook-local.md`).
- [x] Histórico de checklist com rastreabilidade formal (`docs/implementation-checklist.md`).
- [ ] Novas chaves de Testnet segregadas geradas e validadas pelo operador humano.
- [ ] Aprovação humana explícita antes de qualquer mutação de rede externa.

## Condições de rejeição e rollback

Qualquer drift de posição, divergência de saldo, tentativa de retirada habilitada, descarte de auditoria, chave exposta ou envio não autorizado de ordem fora de ambiente local PAPER aborta a promoção e força o sistema para `SafeMode` ou `KillSwitch`.
