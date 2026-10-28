namespace CryptoTrading.Worker;

internal static class WorkerHostFailureSafety
{
    internal static IServiceCollection AddWorkerFailureSafety(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<HostOptions>()
            .Validate(static options => options.BackgroundServiceExceptionBehavior == BackgroundServiceExceptionBehavior.StopHost,
                "Worker background service failures must stop the host.")
            .ValidateOnStart();
        return services;
    }
}
