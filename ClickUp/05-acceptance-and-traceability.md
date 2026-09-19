# Aceite e rastreabilidade local

## Cadeia de evidência

```text
GOAL-001 → E-XX → US-XXX → RF/RNF → AC-* → TASK-* → SUBTASK-* → CODE → TEST → EVIDENCE-*
```

Os vínculos autoritativos estão em [../plan/traceability.yaml](../plan/traceability.yaml)
e os critérios completos em [../plan/18-acceptance-criteria.md](../plan/18-acceptance-criteria.md).
Este arquivo registra o contrato de leitura e o gate local para cada cartão.

## Tipos de evidência obrigatórios

| Prefixo | Evidência |
|---|---|
| `TEST-*` | teste automatizado ou comando executado |
| `EVIDENCE-*` | saída observada, log, snapshot ou relatório versionado |
| `ADR-*` | decisão, opções, trade-offs e consequências |
| `RELEASE-*` | readiness, canary, abort/rollback e pós-release |
| `RECON-*` | comparação local/exchange e compensações |
| `EXPERIMENT-*` | hipótese, trial budget, métricas, holdout e decisão |

## Gate de cartão

Antes de `Ready`, verificar:

- objetivo, ator e valor estão claros;
- escopo e não-escopo impedem expansão;
- regras, autorização e estados de falha estão descritos;
- dependências e parent IDs existem;
- ACs estão vinculados a RF/RNF e são Given/When/Then;
- comando/teste e evidência esperada estão definidos;
- risco residual, rollback e owner estão explícitos;
- source path e commit estão registrados;
- nenhum secret, placeholder ou threshold não aprovado foi promovido.

## Gate de implementação

Para cada story, o implementador deve restatar premissas, apontar lacunas,
propor o menor plano, alterar apenas o escopo aprovado, executar os comandos,
reportar arquivos/resultados/riscos e anexar `TEST-*`/`EVIDENCE-*`. Uma story
fica `Done` somente quando código, testes, observabilidade, segurança,
documentação, migração, rollback e revisão independente estiverem completos.

## Validação do pacote local

```text
19 epics; 96 stories; 233 tasks; 523 subtasks; 43 acceptance criteria
0 IDs duplicados; 0 órfãos; 0 ciclos de dependência; 0 links locais quebrados
```
