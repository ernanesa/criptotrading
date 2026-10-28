using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class WorkerHostFailureSafetyTests
{
    [Fact]
    public async Task StartupRejectsIgnoreEvenWhenConfiguredAfterSafetyRegistration()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddWorkerFailureSafety();
        builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        var probe = new StartupProbe();
        builder.Services.AddSingleton<IHostedService>(probe);
        await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            using var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        });
        Assert.False(probe.Started);
    }

    [Fact]
    public async Task StartupAllowsStopHostAndStartsHostedServices()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddWorkerFailureSafety();
        var probe = new StartupProbe();
        builder.Services.AddSingleton<IHostedService>(probe);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.Started);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BackgroundFailureSignalsHostShutdownWithStopHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddWorkerFailureSafety();
        using var failing = new FailingBackgroundService();
        builder.Services.AddSingleton<IHostedService>(failing);
        using var host = builder.Build();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());

        await host.StartAsync(TestContext.Current.CancellationToken);
        failing.Release.TrySetResult();
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StopAsync(TestContext.Current.CancellationToken));
    }

    private sealed class StartupProbe : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingBackgroundService : BackgroundService
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Release.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            throw new InvalidOperationException("offline-synthetic-service-failure");
        }
    }
}
