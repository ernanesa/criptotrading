---
name: software-development
description: Implement approved software requirements safely and incrementally, with architecture, tests, documentation, security, and validation for .NET/C# and React/TypeScript projects.
metadata:
  short-description: Implement and validate production-quality software
---

# Software Development

Use this skill when the user wants an approved requirement, story, task, bug fix, or technical change implemented in a repository.

## Development contract

Before editing, identify the repository, current branch/state, relevant conventions, dependency versions, acceptance criteria, non-goals, and validation commands. If the requirement is ambiguous or conflicts with the repository, stop and report the exact decision needed.

Restate the implementation scope and assumptions briefly. Make the smallest coherent change that satisfies the contract; do not expand scope or rewrite unrelated code.

## Workflow

1. Inspect the relevant code, tests, configuration, and documentation.
2. Confirm the API, data, UI, security, observability, and failure contracts.
3. Produce a short plan with affected boundaries and risk.
4. Implement in vertical slices that remain testable.
5. Add or update unit, integration, contract, and UI tests as appropriate.
6. Run formatter, linter, type checker, build, and tests relevant to the change.
7. Review the diff for regressions, security issues, accidental scope, and missing documentation.
8. Report changed files, validation results, assumptions, residual risks, and follow-up work.

## .NET/C# standards

Follow the repository's target framework and package versions. Preserve existing architecture and dependency injection patterns. Validate API contracts, authorization, validation, cancellation, async behavior, logging, error mapping, persistence migrations, and backward compatibility. Prefer explicit tests for domain rules and integration tests for persistence and HTTP behavior.

## React/TypeScript standards

Follow the repository's framework, routing, state, styling, and testing conventions. Keep types explicit at boundaries, model loading/empty/error/success states, validate user input, handle accessibility and keyboard behavior, and avoid introducing global state or dependencies without a reason. Test user-visible behavior rather than implementation details.

## AI-assisted implementation

Use Luna for bounded changes with explicit files, contracts, examples, and checks. Use Terra for architecture, cross-cutting changes, migrations, ambiguous behavior, and high-risk security or concurrency work. In either case, require a plan before implementation and evidence after implementation; never accept generated code without repository validation.

## Real implementation gate

Production and Testnet paths must use the configured real adapter and real protocol behavior. Mocks, stubs, fakes, fixtures, and simulators are permitted only in explicitly named tests or paper-trading boundaries and must not be the default runtime dependency.

Reject completion when the diff contains placeholders, hardcoded success, empty handlers, fake endpoints, unreachable integrations, or tests that only prove a mock was called. Require at least one meaningful integration or contract check for every external boundary. If runtime verification cannot run, report `not verified` rather than implying success.

## Safety and external systems

Do not publish, deploy, delete data, alter ClickUp items, or change infrastructure unless the user explicitly authorizes that specific action. Keep secrets out of source and logs. For ClickUp updates, show a before/after diff and request confirmation.

Read [references/delivery-checklist.md](references/delivery-checklist.md) before finalizing a non-trivial change.
