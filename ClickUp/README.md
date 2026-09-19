# CryptoTrading — ClickUp local workspace export

Este diretório é uma representação local, versionada e auditável do Space
`CryptoTrading`. Ele foi preparado para continuar o trabalho enquanto a
integração ClickUp permanece indisponível por limite diário de chamadas.

## Estrutura equivalente ao ClickUp

```text
CryptoTrading (Space)
└── Product Delivery (Folder)
    ├── R0.x–R1.0 — Active Delivery (List)
    ├── R1.1–R1.2 — Post-R1.0 (List)
    ├── Options — Deferred (List)
    └── Decisions, Risks & Evidence (List)
```

O arquivo `00-local-index.md` é o índice navegável. Os três manifestos de
domínio preservam os cartões na hierarquia Epic → Story → Task → Subtask,
incluindo contratos, critérios Given/When/Then, dependências, testes,
evidências, riscos e Definition of Done.

## Inventário congelado

| Tipo | Quantidade | IDs |
|---|---:|---|
| Epic | 19 | `E-00` … `E-18` |
| Story | 96 | `US-001` … `US-183` |
| Technical Task | 233 | `TASK-US-XXX-NN` |
| Verifiable Subtask | 523 | `SUBTASK-TASK-US-XXX-NN-NN` |
| Total de cartões | 871 | contêineres não incluídos |
| Acceptance Criteria | 43 | `AC-*` |

## Convenção de cartão

Cada item deve manter, no mínimo, `Stable ID`, `Item Type`, `Parent ID`,
`Epic ID`, release, scope/status, prioridade, requisitos, ACs, dependências,
contratos, testes, evidências, riscos e rollback. O status de escopo não é o
status operacional do ClickUp:

```text
active   → Proposed → Refinement → Ready → In Progress → In Review → Validation → Done
blocked  → Blocked (com causa, owner e plano de desbloqueio)
deferred → Deferred/Option (sem sprint, prazo ou autorização de implementação)
```

Nenhum cartão representa implementação concluída. `Source Commit` do manifesto
é a referência do commit que gerou esta exportação; evidência real só pode ser
anexada após executar o comando e inspecionar sua saída.

## Índice de arquivos

- [Índice local e roteamento](00-local-index.md)
- [Produto e entrega](01-product-delivery.md)
- [Plataforma, arquitetura, exchange e operações](02-platform-architecture.md)
- [Pesquisa quantitativa, risco e estratégias](03-quant-research.md)
- [Casos de uso e contratos](04-use-cases-and-requirements.md)
- [Aceite e rastreabilidade](05-acceptance-and-traceability.md)
- [Manifesto de importação](06-import-manifest.yaml)
- [Templates aplicáveis](templates/README.md)
- [Snapshot completo da documentação canônica](canonical/README.md)

## Fonte e regra de precedência

Os cartões foram derivados de `plan/` e dos manifestos em `plan/clickup/`.
Quando houver conflito, a precedência é: requisito/ADR aprovado → critério de
aceite → manifesto de domínio → este índice. Mudanças devem atualizar a fonte
canônica e regenerar/revisar o cartão correspondente; IDs nunca são reciclados.

## Segurança operacional

Este pacote não contém credenciais, chaves ou dados live. O ambiente inicial é
local, com .NET 11/C# 15 e React/TypeScript conforme `plan/05-tech-stack.md`.
Paper, Shadow, Binance Spot Testnet, Canary e Production continuam capacidades
distintas; incerteza de estado financeiro bloqueia novas entradas.
