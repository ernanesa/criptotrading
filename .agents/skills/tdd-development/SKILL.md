---
name: tdd-development
description: Implement approved behavior with focused failing tests, minimal production code, and regression coverage in .NET/C# and React/TypeScript projects.
---

# TDD Development

Use when implementing a feature, bug fix, or behavior change with testable acceptance criteria.

1. Translate each acceptance criterion into an observable test.
2. Write the smallest failing test or reproduce the defect.
3. Implement the smallest production change that makes it pass.
4. Refactor while preserving the tests.
5. Add boundary, failure, concurrency, and contract cases when the behavior requires them.
6. Run focused tests, then the broader applicable suite and formatters.

Do not create tests that only mirror implementation details. Report untested acceptance criteria and explain when an integration or contract test is required.
