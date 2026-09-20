# Code review checklist

## Correctness

- Inputs and boundary values are handled.
- Success, failure, timeout, cancellation, retry, and empty paths are correct.
- Existing behavior and public contracts remain compatible.
- State transitions and concurrency assumptions are safe.

## Security

- Authentication and authorization are enforced at the server boundary.
- User-controlled input is validated and safely encoded or parameterized.
- Secrets and personal data do not enter source, logs, errors, or telemetry.
- Files, URLs, redirects, deserialization, and uploads are constrained.

## Data and performance

- Queries are bounded, indexed where needed, and free of accidental N+1 behavior.
- Transactions, idempotency, caching, and consistency are appropriate.
- Async work has cancellation, timeouts, and bounded resource usage.

## Tests and operations

- Tests exercise observable behavior and important negative paths.
- Logs and metrics support diagnosis without leaking sensitive data.
- Migrations, configuration, deployment, and rollback implications are covered.
- Documentation and contracts match the implementation.
