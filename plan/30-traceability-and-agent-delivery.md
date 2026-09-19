# 30 - Rastreabilidade e entrega assistida por agentes

## Cadeia obrigatória

Todo trabalho implementável deve manter a cadeia:

`GOAL → EPIC → US → AC → TASK → SUBTASK → CODE → TEST → EVIDENCE`

IDs são estáveis e não devem ser reutilizados. Mudanças de requisito criam `SUPERSEDES` e preservam a evidência antiga.

Convenção:

- `E-XX`: epic;
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

O manifesto canônico é `traceability.yaml` e segue `templates/traceability-manifest.yaml`; intervalos como `US-001..012` não são referências válidas. O validator deve detectar:

- IDs duplicados ou órfãos;
- AC sem história;
- história sem task/teste;
- requisito crítico sem AC/evidência;
- task sem comando ou resultado esperado;
- fonte superseded ainda usada como autoridade ativa;
- sprint sem item e evidência vinculados;
- IDs fora da convenção ou referências a intervalos implícitos;
- dependências cíclicas;
- artefato crítico obrigatório ausente ou incompleto;
- gate com threshold/período sem `RiskPolicy`, `PromotionReadinessMatrix` ou `CanaryPlan` vinculado.

O primeiro validator executável está em `scripts/validate-traceability.sh` e é
executado na quality gate. Ele valida, sem dependências externas, schema v2,
IDs explícitos de epic/story/task, duplicatas, intervalos implícitos, tasks
presentes no backlog e referências AC/RF/RNF presentes nas fontes canônicas.
Validações de estado `Ready/Done`, ciclos, evidência de runtime e aprovação
humana continuam gates semânticos separados e não são inferidas pelo script.

Um item só pode ser Done com AC, boundary alterado ou motivo explícito de não aplicabilidade, teste/comando, resultado observado, revisão, documentação/migração e risco residual/rollback.

## Ordem de dependências

Antes de paper/shadow/canary, devem estar concluídos e evidenciados:

1. event journal, correlação e idempotência;
2. OMS/reconciliação e RiskPolicy;
3. SafeMode, kill switch e recovery;
4. observabilidade/auditoria e alertas;
5. lineage de strategy/data/config/model;
6. backup/restore e RPO/RTO medidos.

US-013, US-014, US-015, US-024, US-044, US-054, US-113, US-114, US-130, US-134, US-180, US-181 e US-182 são pré-requisitos do canary. Continuous learning — US-170, US-171, US-172, US-173, US-174, US-175, US-176 e US-177 — permanece pós-R1.0.
