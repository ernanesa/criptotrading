---
name: diagnose
description: Diagnose bugs, regressions, failures, and performance problems with reproducible evidence before proposing or applying a fix.
---

# Diagnose

Use for a concrete failure, unexpected behavior, regression, or performance complaint.

- Establish the expected behavior and smallest reproduction.
- Inspect logs, traces, metrics, recent diff, dependency versions, and relevant configuration.
- Separate observed facts from hypotheses.
- Narrow the cause with one controlled experiment at a time.
- State confidence, blast radius, and the safest fix.
- Add a regression test or diagnostic guard before declaring the issue resolved.

Do not hide uncertainty or change unrelated code while investigating.
