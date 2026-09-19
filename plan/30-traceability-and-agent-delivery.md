# 30 - Rastreabilidade e entrega assistida por agentes

## Cadeia obrigatória

Todo trabalho implementável deve manter a cadeia:

`GOAL → EPIC → US → AC → TASK → SUBTASK → CODE → TEST → EVIDENCE`

IDs são estáveis e não devem ser reutilizados. Mudanças de requisito criam `SUPERSEDES` e preservam a evidência antiga.

Convenção:

- `E-XXX`: epic;
- `US-XXX`: user story;
- `AC-XXX`: acceptance criterion;
- `TASK-US-XXX-NN`: task da história;
- `SUBTASK-TASK-US-XXX-NN-NN`: subtask;
- `TEST-<boundary>-<scenario>`: teste;
- `EVIDENCE-<id>-<date>`: comando, saída, artefato ou relatório.

Os `Tasks:` legados em `plan/17-backlog.md` são sementes e só entram em implementação após serem expandidos para esses IDs.

## Card mínimo

Cada história/tarefa deve declarar objetivo, valor, contexto, in-scope, non-goals, regras, contrato, estados normal/empty/validation/conflict/failure/timeout, dependências, boundaries, segurança, observabilidade, risco, AC, testes, comando, evidência, rollback, frescor da fonte e modelo recomendado.

## Revisão independente

O fluxo padrão é:

`draft → schema/lint → revisão de completude → revisão independente de segurança/rastreabilidade → resolução de conflitos → aprovação`

O revisor independente recebe fontes canônicas e não o parecer do primeiro revisor. Claims externos exigem fonte oficial, data e versão. Desconhecidos devem permanecer explícitos.

Nenhum agente publica, altera risco/credenciais ou executa integração externa como consequência implícita de uma revisão.

## Validator e evidência

O validator deve detectar:

- IDs duplicados ou órfãos;
- AC sem história;
- história sem task/teste;
- requisito crítico sem AC/evidência;
- task sem comando ou resultado esperado;
- fonte superseded ainda usada como autoridade ativa;
- sprint sem item e evidência vinculados.

Um item só pode ser Done com AC, boundary alterado ou motivo explícito de não aplicabilidade, teste/comando, resultado observado, revisão, documentação/migração e risco residual/rollback.

## Ordem de dependências

Antes de paper/shadow/canary, devem estar concluídos e evidenciados:

1. event journal, correlação e idempotência;
2. OMS/reconciliação e RiskPolicy;
3. SafeMode, kill switch e recovery;
4. observabilidade/auditoria e alertas;
5. lineage de strategy/data/config/model;
6. backup/restore e RPO/RTO medidos.

US-170 e US-180..182 são pré-requisitos da Research/Live Platform, mesmo permanecendo agrupados em épicos posteriores por compatibilidade histórica.
