# Item templates

## Epic

- Outcome and product value
- Users/stakeholders
- Scope and non-goals
- Success measures
- Stories expected
- Risks and dependencies

## Story

- Title: verb + user-visible outcome
- User and value
- Context and current problem
- Scope / non-goals
- Business rules
- API, data, UI, and authorization contract as applicable
- Loading, empty, validation, conflict, and failure states
- Acceptance criteria: Given / When / Then
- Tests and observable checks
- Technical constraints and affected boundaries
- Dependencies
- Definition of Done

## Technical task

- Concrete implementation result
- Inputs and outputs
- Files or boundaries likely affected
- Algorithm, migration, endpoint, component, or integration details
- Failure handling and observability
- Tests and command to run
- Explicit non-goals

## Subtask

One independently verifiable action, small enough to complete in a short work session. It must identify its parent, completion evidence, and any blocking dependency.

## Prompt handoff to a coding model

Ask the model to first summarize the requirement and assumptions, then produce a minimal plan, implement only the stated scope, run tests/build/lint, and report changed files, checks, and remaining risks. Do not let the model silently invent API fields, business rules, dependencies, or architecture.
