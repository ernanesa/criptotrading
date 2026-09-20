---
name: architecture-review
description: Review or design software architecture for clear boundaries, ownership, contracts, failure handling, and evolvability.
---

# Architecture Review

Use before cross-cutting implementation, major refactoring, new integrations, or when ownership is unclear.

Evaluate boundaries, dependency direction, data ownership, consistency, failure modes, security, observability, operability, test seams, and migration/rollback. For the Binance bot, keep market data, features, strategy, portfolio, risk, execution, exchange adapter, reconciliation, and audit independent.

Produce a decision record with alternatives, trade-offs, invariants, risks, and the smallest reversible next step. Do not recommend patterns by name without explaining the problem they solve.
