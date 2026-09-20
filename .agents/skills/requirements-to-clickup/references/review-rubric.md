# Review rubric

Score each item from 0 to 2:

- **Outcome**: clear user/business result, not an implementation wish.
- **Context**: problem, actor, trigger, and constraints are present.
- **Boundaries**: scope and non-goals prevent accidental expansion.
- **Rules**: business rules and authorization are explicit.
- **Contract**: API/data/UI behavior is precise where relevant.
- **Acceptance**: observable, testable success and failure paths.
- **Testability**: tests and validation commands are named.
- **Decomposition**: item is small enough and dependencies are visible.
- **Consistency**: terminology, status, parent links, and naming are coherent.
- **AI readiness**: enough context for a lower-reasoning coding model without guessing.

Prioritize:

- Critical: missing contract, security rule, destructive behavior, or acceptance path.
- Important: ambiguity, oversized scope, missing failure behavior, missing tests, or unclear ownership.
- Optional: wording, formatting, terminology, or efficiency improvements.
