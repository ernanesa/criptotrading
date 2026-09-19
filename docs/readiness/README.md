# Readiness local

Este diretório contém snapshots versionados dos artefatos de decisão do CryptoTrading. Eles registram o estado observado no commit indicado; não substituem aprovação humana nem autorizam capital real.

## Snapshots e auditoria atual

- `PRM-20260919-foundation.md`, `CANARY-20260919-draft.md`, `RECON-20260919-local.md` e `RELEASE-20260919-foundation.md` são snapshots da fundação congelados no commit `29291c1`.
- `traceability-20260919.yaml` é o manifesto da auditoria corrente no commit de referência registrado nele.
- `AUDIT-20260919-current.md` é a auditoria corrente, com comandos executados e distinção entre evidência local automatizada e gates ainda dependentes de dados/ações externas.

O snapshot histórico continua válido como registro imutável da decisão daquele
momento. Para evitar que seja confundido com o estado atual, qualquer mudança
posterior deve atualizar o manifesto e criar uma nova auditoria; não se deve
alterar retroativamente o commit de referência de um snapshot já publicado.

## Regra de decisão

A decisão corrente permanece `No-Go` para Shadow, Canary e Live. A fundação
local possui Paper, OOS sintético para validação do pipeline, crash recovery,
backup/restore isolado, Paper chaos/soak e adaptadores read-only. Ainda faltam
evidências de promoção: dataset de mercado real OOS/walk-forward, soak Shadow,
reconciliação autenticada contra a conta Testnet, alert delivery, thresholds
aprovados e aprovação humana. Ausência de evidência é falha fechada.

## Validator executável

Antes de propor uma decisão aprovada, execute:

```bash
./scripts/validate-readiness.sh <PRM.md> <CANARY.md> <RISK-POLICY.md>
```

O validator exige decisão/status aprovável, owner e aprovador, data de validade,
thresholds com unidade e janela, hash SHA-256 da `RiskPolicy` e ausência de
placeholders ou checkboxes pendentes. Ele não cria aprovação e deve falhar para
os snapshots atuais enquanto os gates humanos e Testnet permanecerem pendentes.

O contrato automatizado está em `scripts/test-readiness-validator.sh` e é
executado no CI.

Para atualizar um snapshot, copie o arquivo, altere o ID/data, registre commit/comandos/resultados observáveis e obtenha revisão explícita. Nunca preencha threshold com “adequado”, “suficiente” ou `TBD` em uma decisão de promoção.
