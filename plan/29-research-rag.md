# 29 - Research Plane: RAG híbrido e GraphRAG

## Decisão

O RAG pertence exclusivamente ao **Research/Control Plane**. Não participa do hot path de capital, não autoriza risco, não envia ordens e não pode alterar hard limits, circuit breakers ou credenciais.

O MVP será local-first e incremental:

1. busca lexical e citações;
2. embeddings locais opcionais;
3. grafo incremental de requisitos, decisões, backlog, código e testes;
4. reranking/context packs somente após medir falhas de recuperação.

O grafo é uma camada de navegação e consistência, nunca uma fonte de autoridade isolada.

## Fontes canônicas e autoridade

Ordem padrão de autoridade:

1. código, testes e manifests do commit atual;
2. ADRs aceitos e contratos versionados;
3. requisitos e critérios de aceite aprovados;
4. backlog e decisões de planejamento;
5. documentação externa oficial, com versão/data/URL;
6. notas exploratórias e fontes não oficiais, sempre marcadas como não normativas.

Cada fonte deve registrar `project`, `path`, `version/commit`, `hash`, `authority`, `status`, `updated_at` e `freshness`.

## Armazenamento local inicial

SQLite é a fonte local do índice:

- `documents`: fonte, path, versão, hash, autoridade, status e atualização;
- `chunks`: documento, heading, texto, estimativa de tokens e modelo;
- `fts_chunks`: SQLite FTS5 para termos, IDs, rotas e nomes;
- `embeddings`: vetor local indexado por `source_hash + model_name`;
- `entities` e `edges`: relações de requisitos, histórias, tarefas, código, testes e decisões;
- `summaries`: escopo, hash da fonte, texto, modelo e data.
- `traceability.yaml` e artefatos RiskPolicy/Promotion/Canary: relações normativas e gates, preservando status e validade.

Não adicionar vector database hospedado ao MVP. O sistema deve continuar útil somente com FTS5 quando o provedor local de embeddings estiver indisponível.

## Ingestão e GraphRAG incremental

- calcular hash e ignorar fontes inalteradas;
- dividir por headings, contratos, ADRs, classes, componentes e itens de backlog;
- preservar metadados do pai quando um contrato for dividido;
- extrair IDs, links, símbolos, rotas e marcadores de supersession;
- criar relações `CONTAINS`, `IMPLEMENTED_BY`, `AFFECTS`, `DOCUMENTED_BY`, `CONSTRAINS` e `SUPERSEDES`;
- criar relações `SATISFIES`, `VALIDATED_BY`, `EVIDENCED_BY`, `GATES` e `APPROVED_BY` somente quando declaradas no manifesto/artefato;
- invalidar somente chunks, summaries e embeddings afetados;
- quando houver código, extrair símbolos com Roslyn/C# e TypeScript AST, sem inferir relações que não tenham evidência.

## Retrieval seguro

1. Classificar pergunta e bounded context.
2. Aplicar filtros de projeto, versão, status e autoridade antes da busca.
3. Executar busca lexical e semântica em paralelo.
4. Expandir no máximo 1–2 hops ao redor de entidades de alta confiança.
5. Rerankear por relevância, autoridade, compatibilidade e frescor.
6. Retornar o menor context pack suficiente, com path/linha/hash e conflitos.

Requisitos `SUPERSEDED` ficam preservados para auditoria, mas saem do retrieval ativo. Quando não houver evidência suficiente, a resposta deve declarar “não encontrado” ou “não confirmado”.

## Cache, frescor e tokens

- cachear sumários estáveis por hash da fonte;
- reutilizar diffs e context packs em re-reviews;
- invalidar seletivamente após mudança de fonte;
- não cachear status, permissões, assignees, backlog order ou documentação externa volátil sem refresh;
- usar contexto always-on compacto e retrieval sob demanda;
- registrar tokens de entrada/saída, cache hit, fontes recuperadas e custo estimado por tarefa;
- Luna recebe packs limitados; modelos mais capazes recebem expansão de grafo somente para ambiguidade, arquitetura ou risco elevado.

## Fronteira de tarefa e handoff compacto

Cada tarefa começa com um context pack novo e mínimo, formado por:

1. objetivo e restrições explícitas da última solicitação;
2. decisões ativas e itens `SUPERSEDES` ainda relevantes;
3. caminhos/IDs diretamente envolvidos e o commit-base;
4. invariantes, comandos de validação e bloqueios conhecidos.

Ao finalizar, o agente grava somente um handoff redigido com resultado, commit,
arquivos, evidências, lacunas e próxima ação. Logs brutos, tentativas resolvidas
e contexto conversacional não entram no pack seguinte. O índice preserva a
proveniência para auditoria, mas o retrieval ativo exclui `SUPERSEDED` e fontes
sem compatibilidade com o commit atual. Se uma tarefa mudar o escopo, a relação
`SUPERSEDES` deve apontar para a decisão anterior e registrar o motivo.

## Confirmação contra o código atual

Antes de qualquer recomendação de implementação, o agente deve:

- verificar o commit e working tree atuais;
- confirmar arquivos, símbolos, contratos e testes citados;
- distinguir requisito planejado de comportamento implementado;
- relatar conflito entre documentação e código;
- não usar um resumo ou grafo como substituto de evidência do checkout.

## Avaliação e gates

O Research Plane terá um conjunto fixo de avaliação com casos de fonte canônica, conflito, supersession, card incompleto, prompt adversarial e claim sem citação.

Métricas mínimas: recall da fonte canônica, precisão do context pack, coverage de citações, taxa de fonte stale, taxa de claims sem evidência, tokens por tarefa, cache hit e falsos positivos.

Nenhum agente pode publicar, alterar limites de risco, modificar credenciais, enviar ordens ou alterar sistemas externos sem aprovação explícita e fora deste pipeline de retrieval.

O índice deve sinalizar story/AC/task órfão, gate expirado, threshold sem unidade, artefato `Approved` com campo obrigatório vazio e conflito entre Markdown e `traceability.yaml`; ele não corrige automaticamente a fonte canônica.
