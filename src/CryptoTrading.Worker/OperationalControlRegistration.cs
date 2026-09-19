using CryptoTrading.Domain.Risk;
using CryptoTrading.Persistence;
namespace CryptoTrading.Worker;

/// <summary>Registers the worker's fail-closed operational-control audit boundary.</summary>
internal static class OperationalControlRegistration
{
    public static void Add(IServiceCollection services, bool persistenceEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (persistenceEnabled)
        {
            _ = services.AddSingleton<IOperationalControlAuditSink>(serviceProvider =>
                new PostgresOperationalControlAuditSink(
                    serviceProvider.GetRequiredService<PostgresTradingStore>()));
        }
        else
        {
            _ = services.AddSingleton<IOperationalControlAuditSink, InMemoryOperationalControlAuditSink>();
        }

        _ = services.AddSingleton<IOperationalControl>(serviceProvider =>
            new InMemoryOperationalControl(
                new DenyOperationalControlResetAuthorizer(),
                serviceProvider.GetRequiredService<IOperationalControlAuditSink>()));
    }
}
