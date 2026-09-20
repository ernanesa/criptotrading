---
name: code-review
description: Review code and diffs for correctness, security, maintainability, performance, compatibility, tests, and documentation in .NET/C# and React/TypeScript projects without modifying files by default.
metadata:
  short-description: Review code for actionable defects and risks
---

# Code Review

Use this skill when the user asks for a code review, pull request review, diff review, security review, or quality assessment.

## Review contract

Review the requested scope only. Inspect surrounding code, tests, configuration, dependency versions, API contracts, and relevant documentation when needed to establish behavior. Do not edit files, publish comments, merge, or change external systems unless the user explicitly requests that follow-up.

Prioritize findings by user impact and confidence:

- **P0**: exploitable security issue, data loss, production outage, or catastrophic correctness defect.
- **P1**: likely bug, broken contract, authorization flaw, or regression that should block merge.
- **P2**: meaningful reliability, maintainability, performance, testing, or observability problem.
- **P3**: optional improvement or style issue with low immediate impact.

Every finding must include the precise file and line, the behavior that is wrong or risky, why it matters, and a concrete fix. Do not report speculation as a defect; state what evidence is missing.

## Review sequence

1. Establish the diff and affected behavior.
2. Check contracts, invariants, input validation, authorization, and failure paths.
3. Check tests and identify untested behavior, not just missing coverage numbers.
4. Check data access, concurrency, caching, performance, and resource lifetime.
5. Check dependencies, configuration, secrets, logging, telemetry, and compatibility.
6. Check maintainability, naming, duplication, and documentation only after correctness and security.
7. Report findings first, then strengths, residual risks, and validation performed.

## .NET/C# focus

Inspect async cancellation and exception behavior, authorization policies, validation, serialization, nullable contracts, dependency lifetimes, EF Core queries and migrations, transactions, N+1 queries, SQL injection, logging of sensitive data, HTTP retries/timeouts, and backward-compatible API changes.

## React/TypeScript focus

Inspect runtime validation at boundaries, unsafe rendering, authentication and authorization assumptions, stale state, effect dependencies, race conditions, loading/error/empty states, accessibility, keyboard/focus behavior, unnecessary rerenders, untyped escape hatches, and API/client contract drift.

## Reporting

Use concise, actionable findings. If no findings are found, say what was reviewed and what could not be verified. Distinguish defects from recommendations. Never claim tests passed unless they were actually run.

Read [references/review-checklist.md](references/review-checklist.md) for the focused checklist.
