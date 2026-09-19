# ClickUp publication package

## Purpose

Este diretório é o diff revisável entre a documentação canônica em `plan/` e a futura publicação no ClickUp. Ele não substitui requisitos, ADRs, policies ou critérios de aceite; cada cartão aponta para essas fontes e para o commit que as publicou.

## Publication state

- **Prepared at:** 2026-09-19
- **Repository branch:** `main`
- **External mutation:** not executed
- **Reason:** o Workspace conectado possui apenas o Space `Podcast Manager API`; publicar `CryptoTrading` dentro dele sem confirmação misturaria produtos independentes.
- **Required decision:** criar um novo Space `CryptoTrading` pela UI ou autorizar um Folder `CryptoTrading` dentro do Space existente.

## Proposed ClickUp hierarchy

```text
Workspace 9014839042
└── Space: CryptoTrading                         # preferred; not creatable by the available MCP
    └── Folder: Product Delivery
        ├── List: R0.x–R1.0 — Active Delivery
        ├── List: R1.1–R1.2 — Post-R1.0
        ├── List: Options — Deferred
        └── List: Decisions, Risks & Evidence
```

Fallback, somente com aprovação explícita:

```text
Space: Podcast Manager API (90148740091)
└── Folder: CryptoTrading
    ├── List: R0.x–R1.0 — Active Delivery
    ├── List: R1.1–R1.2 — Post-R1.0
    ├── List: Options — Deferred
    └── List: Decisions, Risks & Evidence
```

## Item hierarchy

```text
Epic
└── Story
    └── Technical Task
        └── Verifiable Subtask
```

Use custom item types when they exist. If nested subtasks are unavailable, preserve all four levels with stable IDs plus `Parent ID`, `Epic ID` and task relationships; never flatten away `Task → Subtask` traceability.

## Lists and routing

| List | Content | Initial status |
|---|---|---|
| R0.x–R1.0 — Active Delivery | active epics/stories/tasks required for the local Binance Spot baseline | Proposed; Ready only after DoR |
| R1.1–R1.2 — Post-R1.0 | multi-strategy, regime and continuous learning | Deferred |
| Options — Deferred | cloud, Futures, arbitrage, market making and HA options | Deferred/Option |
| Decisions, Risks & Evidence | decision spikes, ADR follow-ups, risks, release evidence and audit items | Proposed/Open |

## Exact publication inventory

- 19 Epic items;
- 96 Story items: 59 in active scope and 37 `Deferred/Option`;
- 233 Technical Task items;
- 523 verifiable Subtask items;
- 871 total ClickUp items, excluding the four container Lists and optional decision/risk records;
- active Stories start as `Proposed`, except `US-131`, which starts `Blocked`;
- no item starts `Ready`, receives a due date or is assigned to a Sprint during bootstrap.

## Status workflow

`Proposed → Refinement → Ready → In Progress → In Review → Validation → Done`

`Blocked` is a reversible exception state with blocker and owner. `Deferred/Option` remains outside active delivery and receives no Sprint or due date.

`active`, `mixed` e `deferred` são valores de **scope**, não status ClickUp. Na criação, histórias de scope ativo recebem `Proposed`, `US-131` recebe `Blocked` com readiness/approval como condição de desbloqueio, e opções deferred recebem `Deferred/Option`.

## Common fields

- Stable ID
- Item Type
- Parent ID / Epic ID
- Release and Sprint
- Status and priority
- Persona / stakeholder
- Plane and bounded context
- Trading mode / environment
- Risk class
- Requirement IDs
- Acceptance Criteria IDs
- Dependencies / blocked by
- Mandatory artifacts
- Source paths and source commit, injetado a partir do `HEAD` limpo imediatamente antes da publicação
- Evidence ID
- Review required / approver
- Target model: Luna or Terra
- Residual risk and owner
- Rollback / mitigation
- Deferred reason

## Shared card contract

Every active implementation Story must be self-contained enough to answer:

1. who benefits and why;
2. trigger, context and constraints;
3. in-scope and explicit non-goals;
4. business rules and invariants;
5. API/data/UI/authorization contract where applicable;
6. initial, empty, loading, validation, conflict, timeout, failure and recovery states;
7. observability and security expectations;
8. dependencies and rollout order;
9. Given/When/Then acceptance criteria including failure behavior;
10. tests, commands and evidence expected;
11. rollback and residual risks;
12. Definition of Ready and Definition of Done.

Itens `Deferred/Option` são decision packages, não especificações prontas para implementação. Devem conter hipótese/valor, não escopo, riscos, dependências, plano de validação e critério go/no-go; o contrato completo acima passa a ser obrigatório quando a opção entra em Refinement.

Every agent handoff requires the implementer to restate assumptions, identify missing information, propose the smallest plan, implement only the approved scope, run the specified checks and report changed files, observed results and remaining risks.

## Shared Definition of Done

- traceability `GOAL → E → US → AC → TASK → SUBTASK → CODE → TEST → EVIDENCE` is complete;
- relevant build, analyzers, tests and security checks actually ran and outputs were inspected;
- integration evidence uses the real configured boundary; mocks/fakes count only for unit/contract/paper scopes;
- financial events, credentials and live boundaries received independent review;
- observability, failure behavior, documentation and migrations are complete;
- residual risk, rollback and post-release signals are recorded;
- no cloud credential is necessary for the initial local runtime;
- no threshold placeholder is promoted to Ready or Approved.

## Publication files

- `01-product-delivery.md`: ClickUp structure, dashboard/live delivery and deferred product options.
- `02-platform-architecture.md`: governance, foundation, exchange, OMS, security, local runtime and recovery.
- `03-quant-research.md`: risk, data, research, baseline strategy, execution, paper/shadow/Testnet and post-R1.0 quant capabilities.
- `../traceability.yaml`: cobertura autoritativa de epics, stories, scope/status e vínculos já aprovados. Requirements, task e acceptance tornam-se obrigatórios para cada item antes de `Ready`; ausência em item `Proposed/Deferred` é lacuna explícita, não vínculo inferido.

## Pre-publication gates

- all 19 epics and 96 stories appear exactly once in `traceability.yaml`;
- every canonical AC is linked to at least one story;
- active implementation cards meet the review rubric; unresolved decision cards remain Proposed/Blocked;
- deferred options have no Sprint or due date;
- destination Space/Folder/List is confirmed immediately before mutation;
- current ClickUp state is re-read to prevent duplicates;
- exact create/update diff and expected parent relationships are reviewed;
- returned ClickUp IDs/URLs are recorded after publication.
