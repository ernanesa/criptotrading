# Context, RAG, cache, and token budget

## Recommended context layers

1. **Always-on compact context**: product goal, stack, naming conventions, Definition of Done, safety rules, and current feature.
2. **Retrieved context**: only the relevant domain rules, API contracts, architecture decisions, UX patterns, and related accepted cards.
3. **Card context**: the complete implementation contract for the current item.
4. **Task-specific evidence**: files, tests, logs, screenshots, or decisions needed for this task.

## Retrieval policy

- Prefer authoritative and current sources over similar wording.
- Filter by feature, bounded context, source type, version, and status before semantic search.
- Retrieve a small set of high-signal passages; expand only when a contradiction or missing contract is detected.
- Show source references and flag conflicts instead of silently merging them.

## Cache policy

- Cache stable project summaries, glossary entries, architecture decisions, API schemas, and review results.
- Key cache entries by source hash/version and invalidate on change.
- Cache deterministic intermediate artifacts such as normalized requirements and acceptance-test matrices.
- Do not cache volatile ClickUp status, permissions, assignees, comments, or backlog order without a freshness check.

## Model budget policy

- Luna: analysis, normalization, bounded card drafting, rubric checks, and small rewrites with retrieved context.
- Terra: unresolved ambiguity, cross-cutting architecture, conflicting sources, large decomposition, and final review of high-risk items.
- Use a cheap first pass to identify missing information; do not ask a stronger model to compensate for an underspecified card.
- For publication, generate a compact diff and validate only changed fields.

## Quality gate

Do not publish a card unless the reviewer can answer: who benefits, what changes, what is out of scope, how success is observed, how failures behave, how it is tested, what dependencies exist, and what evidence proves completion.
