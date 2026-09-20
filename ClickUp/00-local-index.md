# Índice local do Space `CryptoTrading`

## Product Delivery

### R0.x–R1.0 — Active Delivery

Fonte principal: [01-product-delivery.md](01-product-delivery.md),
[02-platform-architecture.md](02-platform-architecture.md) e
[03-quant-research.md](03-quant-research.md).

Inclui governança, bootstrap, runtime local, contratos de domínio, exchange
read-only, OMS, ledger, reconciliação, RiskPolicy, kill switch, backtest,
baseline Trend Following BTC/USDT, paper, shadow, Testnet, readiness e canary.

### R1.1–R1.2 — Post-R1.0

Inclui regime explicável, autonomia multi-strategy, seleção/utilidade,
alocação dinâmica, execution quality avançada e continuous learning bounded.

### Options — Deferred

Inclui challengers pós-baseline, Futures/hedging, arbitragem, multi-exchange,
market making avançado, cloud e alta disponibilidade. Cada opção permanece um
decision package com hipótese, não-escopo, riscos, validação e go/no-go.

### Decisions, Risks & Evidence

Registra ADRs, decisões superseded, blockers, riscos residuais, ReleaseEvidence,
ReconciliationReports, ExperimentCards, DatasetCards, ModelCards e incidentes.

## Rotas rápidas

| Necessidade | Arquivo |
|---|---|
| Encontrar um cartão por ID | `rg -n "US-061|TASK-US-061|SUBTASK-TASK-US-061" .` |
| Ver todos os critérios | [05-acceptance-and-traceability.md](05-acceptance-and-traceability.md) |
| Entender contratos/atores | [04-use-cases-and-requirements.md](04-use-cases-and-requirements.md) |
| Importar futuramente | [06-import-manifest.yaml](06-import-manifest.yaml) |
| Usar templates | [templates/README.md](templates/README.md) |

## Incremento E-19

O épico `E-19` adiciona `US-190`…`US-199`, com cinco tasks e três subtasks
verificáveis por story. O objetivo é fechar o runtime A2 sem misturar o hot
path com LLM/RAG/MCP e sem promover capital real automaticamente. A fonte
detalhada é [`plan/31-autonomous-runtime-completion.md`](../plan/31-autonomous-runtime-completion.md).

## Parent map

```text
E-XX
└── US-XXX
    └── TASK-US-XXX-NN
        └── SUBTASK-TASK-US-XXX-NN-NN
```

O ID é a autoridade de identidade. Títulos podem melhorar sem quebrar links;
renomear não cria um novo cartão.
