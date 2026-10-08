using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// The assembly targets one explicit test database. Per-test concurrency remains
// enabled inside the fence and reward tests; unrelated scenarios cannot claim
// one another's outbox records or modify the same accounting projection.
[assembly: Parallelization(Mode = ParallelMode.None)]
