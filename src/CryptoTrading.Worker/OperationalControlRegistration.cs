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
            _ = services.AddSingleton<PostgresOperationalControlAuditSink>(serviceProvider =>
                new PostgresOperationalControlAuditSink(
                    serviceProvider.GetRequiredService<PostgresTradingStore>()));
            _ = services.AddSingleton<IOperationalControlAuditSink>(serviceProvider =>
                serviceProvider.GetRequiredService<PostgresOperationalControlAuditSink>());
            _ = services.AddSingleton<IAsyncOperationalControlAuditSink>(serviceProvider =>
                serviceProvider.GetRequiredService<PostgresOperationalControlAuditSink>());
        }
        else
        {
            _ = services.AddSingleton<IOperationalControlAuditSink, InMemoryOperationalControlAuditSink>();
            _ = services.AddSingleton<IAsyncOperationalControlAuditSink>(serviceProvider =>
                serviceProvider.GetRequiredService<IOperationalControlAuditSink>() as IAsyncOperationalControlAuditSink
                ?? throw new InvalidOperationException("The in-memory operational audit sink must support async append."));
        }

        _ = services.AddSingleton<IOperationalControl>(serviceProvider =>
            new InMemoryOperationalControl(
                new DenyOperationalControlResetAuthorizer(),
                serviceProvider.GetRequiredService<IOperationalControlAuditSink>(),
                serviceProvider.GetRequiredService<IAsyncOperationalControlAuditSink>()));
        _ = services.AddSingleton<IAsyncOperationalControl>(serviceProvider =>
            (IAsyncOperationalControl)serviceProvider.GetRequiredService<IOperationalControl>());
    }
}
