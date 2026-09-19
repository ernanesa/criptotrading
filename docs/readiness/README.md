# Readiness local

Este diretório contém snapshots versionados dos artefatos de decisão do CryptoTrading. Eles registram o estado observado no commit indicado; não substituem aprovação humana nem autorizam capital real.

## Snapshot atual

- `PRM-20260919-foundation.md`: matriz de promoção da fundação para Paper/Shadow.
- `CANARY-20260919-draft.md`: plano preliminar de canary; permanece bloqueado e sem capital.
- `RECON-20260919-local.md`: evidência de reconciliação local e limites do que ainda não foi executado contra a Binance Testnet.
- `RELEASE-20260919-foundation.md`: gates executados, evidências e riscos residuais.
- `traceability-20260919.yaml`: vínculo entre capacidades, testes, comandos e pendências.

## Regra de decisão

O snapshot atual é `No-Go` para Shadow, Canary e Live. O código possui fundação local, Paper e adaptadores read-only, mas ainda faltam evidências operacionais (OOS real, soak, restore/restart, reconciliação contra conta Testnet, alertas e drills). Ausência de evidência é falha fechada.

Para atualizar um snapshot, copie o arquivo, altere o ID/data, registre commit/comandos/resultados observáveis e obtenha revisão explícita. Nunca preencha threshold com “adequado”, “suficiente” ou `TBD` em uma decisão de promoção.
