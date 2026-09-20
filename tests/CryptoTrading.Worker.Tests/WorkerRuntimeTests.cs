using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Exchange.Paper;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Configuration;
using Microsoft.Extensions.Configuration;
using CryptoTrading.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class WorkerRuntimeTests
{
    [Fact]
    public void DefaultsArePaperNoTradeAndDoNotRequirePersistence()
    {
        var options = new WorkerRuntimeOptions();

        options.Validate();

        Assert.Equal("PAPER", options.ExecutionMode);
        Assert.True(options.NoTrade);
        Assert.False(options.PersistenceEnabled);
        Assert.False(options.ReconciliationEnabled);
        Assert.False(options.RiskPolicyRequired);
    }

    [Fact]
    public void TestnetExecutionRequiresAllExplicitSafetyGates()
    {
        var valid = new WorkerRuntimeOptions
        {
            ExecutionMode = "TESTNET",
            NoTrade = false,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            TestnetExecutionAcknowledged = true,
            TestnetOrderExecutionEnabled = true
        };

        valid.Validate();

        foreach (var invalid in new[]
        {
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = true, PersistenceEnabled = true,
                ApplyMigrations = true, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = false,
                ApplyMigrations = true, TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = true,
                ApplyMigrations = false, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = true,
                ApplyMigrations = true, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = false, TestnetOrderExecutionEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "PAPER", NoTrade = true,
                TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true
            }
        })
        {
            Assert.Throws<OptionsValidationException>(() => invalid.Validate());
        }
    }

    [Fact]
    public async Task TestnetExecutorLooksUpAmbiguousSubmitBeforeDurableRetry()
    {
        var command = NewCommand();
        var exchange = new AmbiguousTestnetClient(command);
        var executor = new TestnetOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true
            }),
            Options.Create(new TradingOptions
            {
                Binance = new BinanceOptions { AllowedSymbols = "BTCUSDT" }
            }),
            NullLogger<TestnetOutboxMessageExecutor>.Instance);

        await executor.ExecuteAsync(MessageFor(command));

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(1, exchange.QueryCalls);
    }

    [Fact]
    public async Task ExecutionRegistrationKeepsPaperDefaultAndSelectsTestnetOnlyWhenConfigured()
    {
        var paperServices = new ServiceCollection();
        paperServices.AddLogging();
        paperServices.AddSingleton<IPaperExchange, PaperExchange>();
        paperServices.AddSingleton(Options.Create(new WorkerRuntimeOptions()));
        _ = paperServices.AddOutboxExecution(new ConfigurationBuilder().AddInMemoryCollection().Build());
        await using (var paperProvider = paperServices.BuildServiceProvider())
        {
            Assert.IsType<PaperOutboxMessageExecutor>(paperProvider.GetRequiredService<IOutboxMessageExecutor>());
        }

        var testnetValues = new Dictionary<string, string?>
        {
            ["WORKER_EXECUTION_MODE"] = "TESTNET",
            ["TRADING_ENVIRONMENT"] = "TESTNET",
            ["BINANCE_REST_BASE_URL"] = "https://testnet.binance.vision/api",
            ["BINANCE_WS_BASE_URL"] = "wss://testnet.binance.vision/ws",
            ["BINANCE_API_KEY"] = "test-key",
            ["BINANCE_API_SECRET"] = "test-secret",
            ["BINANCE_WITHDRAWALS_ENABLED"] = "false",
            ["BINANCE_ALLOWED_SYMBOLS"] = "BTCUSDT"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(testnetValues).Build();
        var testnetServices = new ServiceCollection();
        testnetServices.AddLogging();
        testnetServices.AddTradingConfiguration(configuration);
        testnetServices.AddSingleton(Options.Create(new WorkerRuntimeOptions
        {
            ExecutionMode = "TESTNET",
            NoTrade = false,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            TestnetExecutionAcknowledged = true,
            TestnetOrderExecutionEnabled = true
        }));
        _ = testnetServices.AddOutboxExecution(configuration);
        _ = testnetServices.AddSingleton<IBinanceSpotOrderClient>(_ => new AmbiguousTestnetClient(NewCommand()));
        await using var testnetProvider = testnetServices.BuildServiceProvider();

        _ = testnetProvider.GetRequiredService<IOptions<TradingOptions>>().Value;
        Assert.IsType<TestnetOutboxMessageExecutor>(testnetProvider.GetRequiredService<IOutboxMessageExecutor>());
    }

    [Fact]
    public async Task OperationalControlUsesInMemoryAuditByDefault()
    {
        var services = new ServiceCollection();
        OperationalControlRegistration.Add(services, persistenceEnabled: false);

        await using var provider = services.BuildServiceProvider();
        var control = provider.GetRequiredService<IOperationalControl>();

        Assert.IsType<InMemoryOperationalControlAuditSink>(
            provider.GetRequiredService<IOperationalControlAuditSink>());
        Assert.IsType<InMemoryOperationalControl>(control);
    }

    [Fact]
    public async Task OperationalControlUsesPostgresAuditOnlyWhenPersistenceIsExplicit()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(_ => new PostgresTradingStore(
            new PostgresPersistenceOptions("Host=127.0.0.1;Port=1;Database=local;Username=local;Password=local")));
        OperationalControlRegistration.Add(services, persistenceEnabled: true);

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresOperationalControlAuditSink>(
            provider.GetRequiredService<IOperationalControlAuditSink>());
    }

    [Fact]
    public void MaxOutboxAttemptsIsBounded()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions { MaxOutboxAttempts = 0 }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions { MaxOutboxAttempts = 101 }.Validate());
    }

    [Fact]
    public void AlertWebhookRequiresExplicitAllowlistAndSafeTimeout()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AlertWebhookUrl = "https://alerts.example.test/hook"
        }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AlertWebhookAllowedHosts = "alerts.example.test"
        }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AlertWebhookUrl = "https://alerts.example.test/hook",
            AlertWebhookAllowedHosts = "alerts.example.test",
            AlertWebhookTimeoutSeconds = 121
        }.Validate());

        new WorkerRuntimeOptions
        {
            AlertWebhookUrl = "https://alerts.example.test/hook",
            AlertWebhookAllowedHosts = "alerts.example.test"
        }.Validate();
    }

    [Fact]
    public void ShadowDrillRequiresNoTradeMode()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ShadowEnabled = true,
            NoTrade = false,
        }.Validate());
    }

    [Fact]
    public void ReconciliationRequiresNoTradeAndPersistence()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationEnabled = true,
            NoTrade = false,
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost",
        }.Validate());

        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationEnabled = true,
            NoTrade = true,
            PersistenceEnabled = false,
        }.Validate());
    }

    [Fact]
    public void RiskPolicyGateRequiresPersistence()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            RiskPolicyRequired = true,
            PersistenceEnabled = false,
        }.Validate());
    }

    [Fact]
    public void ReconciliationSnapshotAgeIsBounded()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationMaxSnapshotAgeSeconds = 0,
        }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationMaxSnapshotAgeSeconds = 86_401,
        }.Validate());
    }

    [Fact]
    public void ReconciliationReportDirectoryIsRequiredWhenEnabled()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationEnabled = true,
            PersistenceEnabled = true,
            PostgresConnectionString = "Host=localhost",
            ReconciliationReportDirectory = " ",
        }.Validate());
    }

    [Fact]
    public void ReconciliationIntervalIsBounded()
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationIntervalMinutes = 0
        }.Validate());
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            ReconciliationIntervalMinutes = 1_441
        }.Validate());
    }

    [Fact]
    public async Task NoTradeExecutorDoesNotParseOrExecutePayload()
    {
        var executor = new PaperOutboxMessageExecutor(
            new PaperExchange(),
            Options.Create(new WorkerRuntimeOptions()),
            NullLogger<PaperOutboxMessageExecutor>.Instance);
        var message = new LeasedOutboxMessage(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            CorrelationId.New(),
            IdempotencyKey.New(),
            "not-json-and-never-executed",
            DateTimeOffset.UtcNow,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "test-worker");

        await executor.ExecuteAsync(message);
    }

    [Fact]
    public async Task PaperExecutorUsesCommandIdentityAndSubmitsWhenTradeGateIsExplicitlyDisabled()
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(
            OrderId.New(),
            ClientOrderId.New(),
            new Symbol("BTCUSDT"),
            OrderSide.Buy,
            OrderType.Market,
            new Quantity(0.01m),
            new Price(10_000m),
            100m,
            correlation,
            idempotency,
            "risk-v1",
            DateTimeOffset.UtcNow);
        var message = new LeasedOutboxMessage(
            Guid.NewGuid(),
            OrderExecutionPipeline.SubmitOrderMessageType,
            correlation,
            idempotency,
            JsonSerializer.Serialize(command),
            command.CreatedAtUtc,
            1,
            command.CreatedAtUtc.AddMinutes(1),
            "test-worker");
        var exchange = new PaperExchange();
        var options = Options.Create(new WorkerRuntimeOptions { NoTrade = false });
        var executor = new PaperOutboxMessageExecutor(exchange, options, NullLogger<PaperOutboxMessageExecutor>.Instance);

        await executor.ExecuteAsync(message);

        var order = Assert.Single(exchange.OpenOrders);
        Assert.Equal(command.ClientOrderId, order.ClientOrderId);
    }

    [Fact]
    public async Task PaperExecutorRecoversAmbiguousTimeoutByClientOrderIdWithoutSubmittingAgain()
    {
        var command = NewCommand();
        var exchange = new SubmitThenTimeoutPaperExchange();
        var message = MessageFor(command);
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await executor.ExecuteAsync(message);

        Assert.Equal(1, exchange.SubmitCalls);
        var order = Assert.Single(exchange.OpenOrders);
        Assert.Equal(command.ClientOrderId, order.ClientOrderId);
    }

    [Fact]
    public async Task PaperExecutorPreservesAmbiguousTimeoutWhenClientOrderIsAbsent()
    {
        var command = NewCommand();
        var exchange = new TimeoutWithoutOrderPaperExchange();
        var message = MessageFor(command);
        var executor = new PaperOutboxMessageExecutor(
            exchange,
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(message));

        Assert.Contains(command.ClientOrderId.Value, exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, exchange.SubmitCalls);
    }

    [Fact]
    public async Task PaperExecutorRejectsUndefinedEnumValues()
    {
        var command = NewCommand();
        var payload = JsonNode.Parse(JsonSerializer.Serialize(command))!.AsObject();
        payload["Side"] = 99;
        var message = MessageForPayload(command, payload.ToJsonString());
        var executor = new PaperOutboxMessageExecutor(
            new PaperExchange(),
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await Assert.ThrowsAsync<JsonException>(() => executor.ExecuteAsync(message));
    }

    [Fact]
    public async Task PaperExecutorRejectsInconsistentAuthorizedNotional()
    {
        var command = NewCommand();
        var payload = JsonNode.Parse(JsonSerializer.Serialize(command))!.AsObject();
        payload["AuthorizedNotional"] = 101m;
        var message = MessageForPayload(command, payload.ToJsonString());
        var executor = new PaperOutboxMessageExecutor(
            new PaperExchange(),
            Options.Create(new WorkerRuntimeOptions { NoTrade = false }),
            NullLogger<PaperOutboxMessageExecutor>.Instance);

        await Assert.ThrowsAsync<JsonException>(() => executor.ExecuteAsync(message));
    }

    private static SubmitOrderCommand NewCommand() => new(
        OrderId.New(),
        ClientOrderId.New(),
        new Symbol("BTCUSDT"),
        OrderSide.Buy,
        OrderType.Market,
        new Quantity(0.01m),
        new Price(10_000m),
        100m,
        CorrelationId.New(),
        IdempotencyKey.New(),
        "risk-v1",
        DateTimeOffset.UtcNow);

    private static LeasedOutboxMessage MessageFor(SubmitOrderCommand command) => new(
        Guid.NewGuid(),
        OrderExecutionPipeline.SubmitOrderMessageType,
        command.CorrelationId,
        command.IdempotencyKey,
        JsonSerializer.Serialize(command),
        command.CreatedAtUtc,
        1,
        command.CreatedAtUtc.AddMinutes(1),
        "test-worker");

    private static LeasedOutboxMessage MessageForPayload(SubmitOrderCommand command, string payload) => new(
        Guid.NewGuid(),
        OrderExecutionPipeline.SubmitOrderMessageType,
        command.CorrelationId,
        command.IdempotencyKey,
        payload,
        command.CreatedAtUtc,
        1,
        command.CreatedAtUtc.AddMinutes(1),
        "test-worker");

    private sealed class SubmitThenTimeoutPaperExchange : IPaperExchange
    {
        private readonly PaperExchange inner = new();

        public int SubmitCalls { get; private set; }

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            var result = inner.Submit(request, idempotencyKey);
            throw new TimeoutException("response lost after exchange acceptance");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => inner.GetOrder(clientOrderId);

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => inner.Cancel(clientOrderId, idempotencyKey);

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => inner.Apply(tick);

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => inner.Replay(ticks);

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => inner.OpenOrders;

        public IReadOnlyCollection<PaperFill> Fills => inner.Fills;
    }

    private sealed class TimeoutWithoutOrderPaperExchange : IPaperExchange
    {
        public int SubmitCalls { get; private set; }

        public PaperSubmitResult Submit(PaperOrderRequest request, IdempotencyKey idempotencyKey)
        {
            SubmitCalls++;
            throw new TimeoutException("response lost before exchange acceptance");
        }

        public PaperOrderSnapshot GetOrder(ClientOrderId clientOrderId) => throw new KeyNotFoundException();

        public PaperCancelResult Cancel(ClientOrderId clientOrderId, IdempotencyKey idempotencyKey) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Apply(PaperMarketTick tick) => throw new NotSupportedException();

        public IReadOnlyList<PaperFill> Replay(IEnumerable<PaperMarketTick> ticks) => throw new NotSupportedException();

        public IReadOnlyCollection<PaperOrderSnapshot> OpenOrders => [];

        public IReadOnlyCollection<PaperFill> Fills => [];
    }

    private sealed class AmbiguousTestnetClient(SubmitOrderCommand command) : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            throw new BinanceOrderSubmissionAmbiguousException(
                request.ClientOrderId,
                new IOException("simulated response loss"));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(
                42,
                command.Symbol.Value,
                command.ClientOrderId.Value,
                "NEW",
                command.ReferencePrice.Value,
                command.Quantity.Value,
                0m,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
