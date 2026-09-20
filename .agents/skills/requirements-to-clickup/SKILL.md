---
name: requirements-to-clickup
description: Analyze software requirements and turn them into implementation-ready epics, stories, tasks, subtasks, specs, and acceptance criteria for .NET/C# and React/TypeScript projects, with optional reviewed publication to ClickUp.
metadata:
  short-description: Requirements to AI-ready ClickUp backlog
---

# Requirements to ClickUp

Use this skill when the user wants to refine requirements, design a backlog, write implementation-ready work items, or synchronize reviewed items with ClickUp.

## Operating modes

1. **Analyze**: extract actors, goals, constraints, assumptions, risks, dependencies, and unanswered questions. Do not create or edit external items.
2. **Design backlog**: propose product goal, epics, stories, technical specs, tasks, subtasks, and ordering. Keep stories focused on user value; put implementation detail in the spec and tasks.
3. **Review**: inspect existing ClickUp items read-only and report ambiguity, missing context, weak acceptance criteria, duplication, inconsistent vocabulary, and suggested rewrites.
4. **Publish**: only after the user explicitly approves the proposed diff. Show the exact items, destination list, parent relationships, and fields that will be written before mutating ClickUp.

## AI-ready item standard

Every story intended for coding should contain: objective and user value, context, in-scope and out-of-scope behavior, business rules, API/data/UI contract as applicable, error and empty states, security/authorization, dependencies, acceptance criteria in Given/When/Then form, tests, technical constraints, affected boundaries, and Definition of Done.

Use a short technical spec when the story involves an API contract, persistence, integration, authorization, asynchronous behavior, UI state, migration, or a non-obvious architectural decision. Do not create a separate use case for a simple CRUD flow; use one for complex actors, permissions, alternate paths, or business rules.

## Model routing

- Luna: bounded, single-purpose work with explicit files, contracts, examples, acceptance criteria, and test commands.
- Terra: architecture, cross-cutting changes, migrations, ambiguous requirements, performance/security trade-offs, or multi-service refactors.

Before asking a model to code, require it to restate assumptions, identify missing information, propose a plan, implement only the approved scope, run the stated checks, and report changed files and residual risks.

## SOTA quality with token efficiency

Use a staged workflow instead of sending the entire project to every model call: classify the request, retrieve only the relevant product/domain/technical context, draft the smallest useful artifact, run a rubric-based review, and expand only the weak sections. Reuse cached summaries for stable architecture, vocabulary, conventions, and Definition of Done; invalidate them when the source documents or repository contracts change. Prefer diffs and targeted re-reviews over regenerating complete cards.

RAG is useful when the project has enough stable source material: glossary, Product Goal, architecture decisions, API contracts, domain rules, UI conventions, security policies, and previous accepted cards. Index source documents with metadata such as source, version, date, feature, bounded context, and authority. Retrieve authoritative sources first, then recent decisions, then related examples. Never use retrieved text as permission to publish or as proof that an assumption is true; surface conflicts and ask for resolution.

Keep the ClickUp card self-contained enough for a coding model. RAG and cache reduce prompt size, but they do not replace essential context in the card: objective, scope, contracts, acceptance criteria, tests, constraints, and Definition of Done.

## ClickUp safety

MCP reads are allowed during Analyze and Review. Creating, editing, moving, or deleting ClickUp items is allowed only in Publish after explicit approval. Never infer permission to publish from a request to analyze or draft. Preserve the original text when proposing changes and show a before/after diff.

Use [references/roles.md](references/roles.md) to separate analyst, product, architecture, specification, review, and ClickUp publication responsibilities. Read [references/item-templates.md](references/item-templates.md) for the item schema, [references/review-rubric.md](references/review-rubric.md) for quality scoring, and [references/context-strategy.md](references/context-strategy.md) when designing retrieval, caching, or token budgets.
