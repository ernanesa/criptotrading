# Release Evidence RELEASE-20260919-foundation

**Versão/commit:** `29291c1`
**Ambiente/modo:** máquina local; PAPER/NO_TRADE por padrão
**Owner/revisor/aprovador:** Codex / pendente / pendente
**Decisão:** **No-Go para Shadow/Canary/Live; fundação local aceita para desenvolvimento**

## Rastreabilidade

- requisitos e gates: `plan/traceability.yaml`, `plan/18-acceptance-criteria.md`;
- checklist operacional: `docs/implementation-checklist.md`;
- readiness: `PRM-20260919-foundation.md`, `CANARY-20260919-draft.md`, `RECON-20260919-local.md`;
- ADRs e risco: `plan/24-adrs.md`, `plan/21-risk-register.md`.

## Gates executados

| Gate/comando | Resultado | Evidência |
|---|---|---|
| `dotnet restore CryptoTrading.slnx --locked-mode` | PASS | dependências restauradas |
| `dotnet build CryptoTrading.slnx --no-restore --configuration Release` | PASS: 0 warnings, 0 errors | build Release |
| `dotnet test CryptoTrading.slnx --no-build --no-restore --configuration Release --verbosity minimal` | PASS | todos os projetos da solução verdes |
| `dotnet format CryptoTrading.slnx --no-restore --verify-no-changes` | PASS | sem alterações de formatação |
| `./scripts/scan-secrets.sh` | PASS | nenhum segredo versionado |
| `POSTGRES_PASSWORD=validation-only-not-a-credential docker compose config --quiet` | PASS | Compose válido |
| `./scripts/test-postgres-integration.sh` | PASS anteriormente | migration, ledger, outbox lease/retry/idempotência; commit `f7b073a` |
| Binance authenticated integration / Testnet submit | **NÃO EXECUTADO** | nenhuma chave privada usada; nenhuma ordem enviada |
| restore/restart/crash, chaos, soak e alert delivery | **NÃO EXECUTADO** | pendência operacional |

## Deploy, rollback e riscos residuais

Não há deploy externo. O rollback deve preservar journal, ledger, outbox e dados de mercado; não executar rollback destrutivo de banco. Riscos residuais: ausência de OOS/walk-forward aprovado, reconciliação autenticada não executada, ausência de restore/chaos/soak, thresholds de RiskPolicy/PRM/Canary ainda não aprovados e frontend/dashboard operacional incompletos.

## Próximos gates obrigatórios

1. fechar os contratos persist-before-submit, timeout ambíguo e restart recovery;
2. executar dataset OOS/walk-forward/sensitivity/stress com artefatos versionados;
3. preencher RiskPolicy/PRM/Canary com números, unidades e validade;
4. usar novas credenciais Testnet somente após confirmação das permissões e produzir reconciliação redigida;
5. executar restore, kill-switch, SafeMode, alertas, chaos e soak; obter aprovação humana antes de qualquer transição.
