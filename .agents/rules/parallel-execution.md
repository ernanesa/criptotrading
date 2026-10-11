---
description: Mandatory agent parallelism and subagent orchestration for all tasks in CryptoTrading.
alwaysApply: true
---

# Mandatory Parallelism & Subagent Orchestration

- **PARALELISMO OBRIGATÓRIO**: Decomponha e paralelize imediatamente toda tarefa que admitir tarefas concorrentes ou independentes utilizando agentes e subagentes (`invoke_subagent`).
- Em toda demanda de funcionalidade, auditoria, refatoração ou diagnóstico:
  1. Lance subagentes especializados em paralelo (ex: exploração de código/arquitetura, análise de segurança e concorrência, formulação de testes TDD, documentação).
  2. Subagentes de pesquisa e validação executam de forma somente-leitura e reportam achados diretamente ao orquestrador.
  3. O agente integrador principal é o único escritor de arquivos compartilhados para evitar condições de corrida (race conditions).
- Utilize o Cognitive Model Router:
  - `flash` para buscas, inventários, varreduras mecânicas e testes rápidos.
  - `pro` para decisões arquiteturais, limites de risco financeiro, concorrência no bot e diagnósticos complexos.
- Nenhuma etapa com sub-tarefas independentes deve ser executada de forma sequencial quando puder rodar concorrentemente.
