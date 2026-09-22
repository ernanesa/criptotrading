# Release Evidence RELEASE-20260921-autonomy-completion

**Versão / Commit:** `efc6826` (HEAD main)
**Ambiente / Modo:** PAPER / SHADOW (Local isolated; NO_TRADE)
**Owner / Revisor / Aprovador:** Autonomous Runtime Agent / Codex / Operador Humano (Pendente)
**Decisão:** **Go para execução local Paper/Shadow; No-Go estrito para Testnet/Live**

## Rastreabilidade

- **Meta / Épico:** Epic E-19 — Fechamento do Runtime Autônomo
- **Histórias Cobertas:** US-190, US-191, US-192, US-193, US-194, US-195, US-196, US-197, US-198, US-199
- **Manifesto de Rastreabilidade:** `plan/traceability.yaml` (126 itens mapeados, schema v2)
- **Checklist de Implementação:** `docs/implementation-checklist.md`
- **Promotion Readiness Matrix:** `docs/readiness/PRM-20260921-autonomy-completion.md`
- **Runbook Operacional:** `docs/runbook-local.md`

## Gates Executados

| Gate / Comando | Resultado | Evidência / Artefato | Revisor |
|---|---|---|---|
| `dotnet build --no-incremental` | PASS (0 warnings, 0 errors) | Compilação completa de todos os 25 projetos da solução | Codex |
| `dotnet test` (Solução inteira) | PASS (510/510 testes passaram) | Suíte Domain (157), Worker (85), Binance (88), Backtesting (61), Observability (38), Config (22), Reconcil (20), Persistence (19), Shadow (12), Paper (8) | Codex |
| `./scripts/scan-secrets.sh` | PASS | 0 segredos detectados em arquivos rastreados | Codex |
| `./scripts/validate-traceability.sh` | PASS | 126 explicit item IDs, schema v2, 0 órfãos | Codex |
| `tools/CryptoTrading.AutonomyDecisionReconstructor --help` | PASS | CLI tool compilado e executável para auditoria de decisões | Codex |
| Isolamento de Testnet / Live | PRESERVADO | Zero requisições de escrita, zero ordens enviadas | Codex |

## Implantação e Rollback

- **Tratamento de Ordens em Voo:**
  - Presença de ordens locais em status `UNKNOWN` bloqueia automaticamente qualquer nova exposição no ciclo (`UNKNOWN_ORDER_EXISTS`).
  - Cancelamento periódico de ordens expiradas atua com TTL determinístico em ordens passivas pendentes.
- **Compatibilidade de Banco de Dados:**
  - Todas as migrações até `025_autonomy_decision_commits.up.sql` são idempotentes e append-only. Triggers impedem `UPDATE` e `DELETE` no journal de decisões.
- **Procedimento de Rollback:**
  - Em caso de falha operacional, o supervisor é direcionado a `SafeMode` ou `KillSwitch` via `FailureClassifier`.
  - Checkpoint reidratado duravelmente inicializa o Worker em `SafeMode`, exigindo reconciliação e warm-up antes de retomar decisões.

## Riscos Residuais e Pós-Release

1. **Credenciais de Testnet:** Novas chaves da Binance Spot Testnet com restrição de IP e retiradas desabilitadas ainda devem ser provisionadas pelo operador antes de qualquer teste de rede real.
2. **Soak Drill em Rede Externa:** Ensaios prolongados contra a Binance Testnet devem ser realizados exclusivamente após aprovação explícita e em horário monitorado.
