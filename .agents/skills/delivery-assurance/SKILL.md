---
name: delivery-assurance
description: Enforce traceability and quality gates from requirements through tests, code review, documentation, and delivery for software projects.
metadata:
  short-description: Verify the full software delivery chain
---

# Delivery Assurance

Use this skill after requirements are drafted and during implementation, validation, verification, review, and release preparation.

## Required chain

Maintain traceability from Product Goal and epic to story, acceptance criterion, task, code boundary, test, review finding, and delivery evidence. Missing links are findings, not assumptions.

## Gates

Run the gates applicable to the repository: dependency/version check, formatting, lint, type check, build, unit tests, integration/contract tests, security/dependency scan, API/schema validation, accessibility checks, migration review, and documentation consistency. Do not claim a gate passed unless it actually ran and its output was inspected.

## Evidence-first anti-hallucination gate

Do not accept an implementation report as evidence. Inspect the diff and run the relevant checks independently. Search for placeholders, hardcoded responses, fake endpoints, test-only adapters in production wiring, and tests that cannot observe real behavior. Every material claim must point to a file, command output, test result, or versioned official source. Mark unavailable checks as `not verified`; unresolved evidence blocks delivery.

## Verification order

1. Validate the requirement and acceptance criteria.
2. Verify implementation behavior with automated tests.
3. Review the diff for correctness, security, performance, and scope.
4. Verify documentation, contracts, and traceability.
5. Record residual risks, rollback considerations, and post-release signals.

## Release safety

Keep deployment, ClickUp mutation, data deletion, and external publication separate from validation. Request explicit approval immediately before those actions. Generate a compact evidence report and a before/after diff for external updates.

Read [references/traceability.md](references/traceability.md) for the evidence model.
