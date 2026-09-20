---
name: agent-legibility-review
description: Review a repository for whether an AI coding agent can discover, understand, safely change, test, and verify it.
---

# Agent Legibility Review

Inspect repository instructions, module boundaries, naming, dependency manifests, commands, tests, fixtures, observability, generated files, and documentation.

Report blockers such as missing build commands, hidden environment assumptions, ambiguous ownership, stale instructions, oversized modules, untestable side effects, undocumented invariants, and absent failure evidence. Recommend small changes that reduce agent context and error rate. Verify recommendations against the actual repository instead of inventing conventions.
