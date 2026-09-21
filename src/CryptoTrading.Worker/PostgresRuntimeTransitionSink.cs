using CryptoTrading.Domain.Runtime;
using CryptoTrading.Persistence;

namespace CryptoTrading.Worker;

/// <summary>PostgreSQL-backed append-only runtime transition sink.</summary>
internal sealed class PostgresRuntimeTransitionSink(IRuntimeStateStore store) : IRuntimeTransitionSink
{
    public ValueTask AppendAsync(RuntimeStateTransition transition, CancellationToken cancellationToken = default) =>
        new(store.AppendRuntimeTransitionAsync(transition, cancellationToken));
}
