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
            TestnetOrderExecutionEnabled = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        };

        valid.Validate();

        foreach (var invalid in new[]
        {
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = true, PersistenceEnabled = true,
                ApplyMigrations = true, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true, ReconciliationEnabled = true, AccountStreamEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = false,
                ApplyMigrations = true, TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true, ReconciliationEnabled = true, AccountStreamEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = true,
                ApplyMigrations = false, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true, TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true, ReconciliationEnabled = true, AccountStreamEnabled = true
            },
            new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET", NoTrade = false, PersistenceEnabled = true,
                ApplyMigrations = true, PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = false, TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true, ReconciliationEnabled = true, AccountStreamEnabled = true
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
        var store = new RecordingOpenOrderStore();
        var projection = new OrderProjectionService(store);
        var executor = new TestnetOutboxMessageExecutor(
            exchange,
            new TestnetAccountClient(),
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true,
                ReconciliationEnabled = true,
                AccountStreamEnabled = true
            }),
            Options.Create(new TradingOptions
            {
                Binance = new BinanceOptions { AllowedSymbols = "BTCUSDT" }
            }),
            projection,
            NullLogger<TestnetOutboxMessageExecutor>.Instance,
            submissionGuard: new TestSubmissionGuard(), submissionStore: new TestSubmissionStore());

        await executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(1, exchange.QueryCalls);
        var projected = Assert.Single(store.Records);
        Assert.Equal("NEW", projected.Status);
        Assert.Equal(command.ClientOrderId.Value, projected.ClientOrderId);
    }

    [Fact]
    public async Task TestnetExecutorQuarantinesAmbiguousSubmitWhenOutcomeCannotBeQueried()
    {
        var command = NewCommand();
        var exchange = new UnknownOutcomeTestnetClient();
        var store = new RecordingOpenOrderStore();
        var projection = new OrderProjectionService(store);
        var executor = new TestnetOutboxMessageExecutor(
            exchange,
            new TestnetAccountClient(),
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true,
                ReconciliationEnabled = true,
                AccountStreamEnabled = true
            }),
            Options.Create(new TradingOptions { Binance = new BinanceOptions { AllowedSymbols = "BTCUSDT" } }),
            projection,
            NullLogger<TestnetOutboxMessageExecutor>.Instance,
            submissionGuard: new TestSubmissionGuard(), submissionStore: new TestSubmissionStore());

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(1, exchange.QueryCalls);
        var projected = Assert.Single(store.Records);
        Assert.Equal("UNKNOWN", projected.Status);
    }

    [Fact]
    public async Task TestnetExecutorProjectsDefinitiveExchangeRejectionWithoutRetry()
    {
        var command = NewCommand();
        var exchange = new RejectedTestnetClient();
        var store = new RecordingOpenOrderStore();
        var executor = CreateTestnetExecutor(exchange, new TestnetAccountClient(), store);

        await executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(0, exchange.QueryCalls);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task TestnetExecutorProjectsPreTradeValidationRejectionWithoutRetry()
    {
        var command = NewCommand();
        var exchange = new PreTradeRejectedClient();
        var store = new RecordingOpenOrderStore();
        var executor = CreateTestnetExecutor(exchange, new TestnetAccountClient(), store);

        await executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exchange.SubmitCalls);
        Assert.Equal(0, exchange.QueryCalls);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task TestnetExecutorThrowsExecutionDispatchBlockedWhenSymbolNotAllowed()
    {
        var command = NewCommand("ETHUSDT");
        var exchange = new RejectedTestnetClient();
        var store = new RecordingOpenOrderStore();
        var executor = CreateTestnetExecutor(exchange, new TestnetAccountClient(), store);

        var ex = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(
            () => executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("outside BINANCE_ALLOWED_SYMBOLS", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, exchange.SubmitCalls);
    }

    [Fact]
    public async Task TestnetCancelExecutorThrowsExecutionDispatchBlockedWhenSymbolNotAllowed()
    {
        var command = NewCancelCommand("ETHUSDT");
        var store = new RecordingOpenOrderStore();
        var exchange = new CancelRecordingTestnetClient(command, "CANCELED");
        var executor = CreateTestnetCancelExecutor(exchange, store);

        var ex = await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(
            () => executor.ExecuteAsync(CancelMessageFor(command), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("outside BINANCE_ALLOWED_SYMBOLS", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, exchange.CancelCalls);
    }

    [Fact]
    public async Task TestnetCancelExecutorPersistsPendingThenProjectsVenueCancellation()
    {
        var command = NewCancelCommand();
        var store = new RecordingOpenOrderStore();
        await store.UpsertOpenOrderAsync(new LocalOpenOrderRecord("BTCUSDT", 42, command.ClientOrderId.Value, "NEW", DateTimeOffset.UtcNow, 0.01m, 0m), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = new CancelRecordingTestnetClient(command, "CANCELED", queryStatus: "NEW");
        var executor = CreateTestnetCancelExecutor(exchange, store);

        await executor.ExecuteAsync(CancelMessageFor(command), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exchange.CancelCalls);
        Assert.Equal(1, exchange.QueryCalls);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task TestnetCancelExecutorQueriesAfterAmbiguousCancelAndNeverRepeatsIt()
    {
        var command = NewCancelCommand();
        var store = new RecordingOpenOrderStore();
        await store.UpsertOpenOrderAsync(new LocalOpenOrderRecord("BTCUSDT", 42, command.ClientOrderId.Value, "PARTIALLY_FILLED", DateTimeOffset.UtcNow, 0.01m, 0.004m), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = new CancelRecordingTestnetClient(command, "CANCELED", cancelFails: true, queryStatus: "PARTIALLY_FILLED");
        var executor = CreateTestnetCancelExecutor(exchange, store);

        await executor.ExecuteAsync(CancelMessageFor(command), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, exchange.CancelCalls);
        Assert.Equal(2, exchange.QueryCalls);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task TestnetCancelExecutorQuarantinesUnknownCancelOutcomeWhenLookupFails()
    {
        var command = NewCancelCommand();
        var store = new RecordingOpenOrderStore();
        await store.UpsertOpenOrderAsync(new LocalOpenOrderRecord("BTCUSDT", 42, command.ClientOrderId.Value, "NEW", DateTimeOffset.UtcNow, 0.01m, 0m), cancellationToken: TestContext.Current.CancellationToken);
        var exchange = new CancelRecordingTestnetClient(command, "NEW", cancelFails: true, queryFails: true);
        var executor = CreateTestnetCancelExecutor(exchange, store);

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => executor.ExecuteAsync(CancelMessageFor(command), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, exchange.CancelCalls);
        Assert.Equal(2, exchange.QueryCalls);
        var projected = Assert.Single(store.Records);
        Assert.Equal("UNKNOWN", projected.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestnetExecutorBlocksMissingOrExpiredCommissionSnapshotBeforeSubmission(bool expired)
    {
        var command = expired
            ? NewCommand() with { CommissionValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }
            : NewCommand() with { EstimatedCommissionRate = null };
        var exchange = new RecordingTestnetClient(command);
        var executor = CreateTestnetExecutor(exchange, new TestnetAccountClient(), new RecordingOpenOrderStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("commission snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, exchange.SubmitCalls);
    }

    [Theory]
    [InlineData("99", "1", true, false, false, "quote")]
    [InlineData("1000", "0.001", true, false, true, "base")]
    [InlineData("1000", "1", false, false, false, "capabilities")]
    [InlineData("1000", "1", true, true, false, "capabilities")]
    public async Task TestnetExecutorBlocksUnsafePreSubmitBalanceOrCapability(
        string quoteFree, string baseFree, bool canTrade, bool canWithdraw, bool sell, string expectedReason)
    {
        var command = sell ? NewCommand() with { Side = OrderSide.Sell } : NewCommand();
        var exchange = new RecordingTestnetClient(command);
        var executor = CreateTestnetExecutor(
            exchange,
            new TestnetAccountClient(baseFree, quoteFree, canTrade, canWithdraw),
            new RecordingOpenOrderStore());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(MessageFor(command), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(expectedReason, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, exchange.SubmitCalls);
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
            TestnetOrderExecutionEnabled = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        }));
        testnetServices.AddSingleton<ILocalOpenOrderStore, RecordingOpenOrderStore>();
        testnetServices.AddSingleton<IDurableSubmissionStore, TestSubmissionStore>();
        testnetServices.AddSingleton<IOrderCancellationStore>(serviceProvider =>
            new RecordingCancellationStore(serviceProvider.GetRequiredService<ILocalOpenOrderStore>()));
        testnetServices.AddSingleton<OrderProjectionService>();
        _ = testnetServices.AddOutboxExecution(configuration);
        testnetServices.AddSingleton<IExecutionSubmissionGuard, TestSubmissionGuard>();
        _ = testnetServices.AddSingleton<IBinanceSpotOrderClient>(_ => new AmbiguousTestnetClient(NewCommand()));
        _ = testnetServices.AddSingleton<IBinanceSpotAccountClient, TestnetAccountClient>();
        await using var testnetProvider = testnetServices.BuildServiceProvider();

        _ = testnetProvider.GetRequiredService<IOptions<TradingOptions>>().Value;
        var executors = testnetProvider.GetServices<IOutboxMessageExecutor>();
        Assert.Contains(executors, executor => executor is TestnetOutboxMessageExecutor);
        Assert.Contains(executors, executor => executor is TestnetCancelOutboxMessageExecutor);
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
    public void PaperReconciliationRequiresNoTradeAndPersistence()
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
    public void TestnetExecutionRequiresRiskReconciliationAndAccountStream()
    {
        WorkerRuntimeOptions Valid() => new()
        {
            ExecutionMode = "TESTNET",
            NoTrade = false,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            TestnetExecutionAcknowledged = true,
            TestnetOrderExecutionEnabled = true,
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true
        };

        Valid().Validate();

        foreach (var mutate in new Action<WorkerRuntimeOptions>[]
        {
            options => options.RiskPolicyRequired = false,
            options => options.ReconciliationEnabled = false,
            options => options.AccountStreamEnabled = false
        })
        {
            var options = Valid();
            mutate(options);
            Assert.Throws<OptionsValidationException>(() => options.Validate());
        }
    }

    [Fact]
    public void AutonomyRequiresACompleteDurableSafetyEnvelope()
    {
        var valid = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            ExecutionMode = "PAPER",
            NoTrade = true
        };
        valid.Validate();

        var missingStream = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = false
        };
        Assert.Throws<OptionsValidationException>(() => missingStream.Validate());

        var invalidInterval = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomyInterval = "30s"
        };
        Assert.Throws<OptionsValidationException>(() => invalidInterval.Validate());
    }

    [Fact]
    public void AutonomySymbolsResolvesAndValidatesAllowedUniverse()
    {
        var validMulti = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomySymbols = "btcusdt, ethusdt; solusdt bnbusdt"
        };
        validMulti.Validate();

        var resolved = validMulti.ResolveAutonomySymbols();
        Assert.Equal(4, resolved.Count);
        Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT"], resolved);

        var fallbackLegacy = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomySymbol = "ETHUSDT"
        };
        fallbackLegacy.Validate();
        Assert.Equal(["ETHUSDT"], fallbackLegacy.ResolveAutonomySymbols());

        var unauthorizedLegacy = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomySymbol = "XRPUSDT"
        };
        var exLegacy = Assert.Throws<OptionsValidationException>(() => unauthorizedLegacy.Validate());
        Assert.Contains("unauthorized symbol 'XRPUSDT'", exLegacy.Message, StringComparison.Ordinal);

        var unauthorized = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomySymbols = "BTCUSDT, DOGEUSDT"
        };
        var ex = Assert.Throws<OptionsValidationException>(() => unauthorized.Validate());
        Assert.Contains("unauthorized symbol 'DOGEUSDT'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomyFeeReserveBpsIsBounded()
    {
        var negative = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomyFeeReserveBps = -1m
        };
        Assert.Throws<OptionsValidationException>(() => negative.Validate());

        var excessive = new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            PersistenceEnabled = true,
            ApplyMigrations = true,
            PostgresConnectionString = "Host=localhost",
            RiskPolicyRequired = true,
            ReconciliationEnabled = true,
            AccountStreamEnabled = true,
            AutonomyFeeReserveBps = 10_000.01m
        };
        Assert.Throws<OptionsValidationException>(() => excessive.Validate());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(10000.01)]
    public void AutonomyFeeDriftToleranceBpsIsBounded(decimal value)
    {
        Assert.Throws<OptionsValidationException>(() => new WorkerRuntimeOptions
        {
            AutonomyFeeDriftToleranceBps = value
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

        await executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken);
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

        await executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken);

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

        await executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken);

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

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken));

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

        await Assert.ThrowsAsync<JsonException>(() => executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken));
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

        await Assert.ThrowsAsync<JsonException>(() => executor.ExecuteAsync(message, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static SubmitOrderCommand NewCommand(string symbol = "BTCUSDT") => new(
        OrderId.New(),
        ClientOrderId.New(),
        new Symbol(symbol),
        OrderSide.Buy,
        OrderType.Market,
        new Quantity(0.01m),
        new Price(10_000m),
        100m,
        CorrelationId.New(),
        IdempotencyKey.New(),
        "risk-v1",
        DateTimeOffset.UtcNow,
        0.001m,
        "test-commission-snapshot",
        "test:commission",
        DateTimeOffset.UtcNow.AddSeconds(-1),
        DateTimeOffset.UtcNow.AddMinutes(1));

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

    private static CancelOrderCommand NewCancelCommand(string symbol = "BTCUSDT") => new(
        OrderId.New(), ClientOrderId.New(), new Symbol(symbol), OrderSide.Buy, OrderType.Limit,
        new Quantity(0.01m), new Quantity(0m), new Price(10_000m), CorrelationId.New(), IdempotencyKey.New(),
        0, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), "42");

    private static LeasedOutboxMessage CancelMessageFor(CancelOrderCommand command) => new(
        Guid.NewGuid(), CancelLifecyclePlanner.CancelOrderMessageType, command.CorrelationId, command.IdempotencyKey,
        JsonSerializer.Serialize(command), command.RequestedAtUtc, 1, command.RequestedAtUtc.AddMinutes(1), "test-worker");

    private static TestnetOutboxMessageExecutor CreateTestnetExecutor(
        IBinanceSpotOrderClient exchange,
        IBinanceSpotAccountClient account,
        ILocalOpenOrderStore store) => new(
            exchange,
            account,
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true,
                ReconciliationEnabled = true,
                AccountStreamEnabled = true
            }),
            Options.Create(new TradingOptions { Binance = new BinanceOptions { AllowedSymbols = "BTCUSDT" } }),
            new OrderProjectionService(store),
            NullLogger<TestnetOutboxMessageExecutor>.Instance,
            submissionGuard: new TestSubmissionGuard(), submissionStore: new TestSubmissionStore());

    private static TestnetCancelOutboxMessageExecutor CreateTestnetCancelExecutor(
        IBinanceSpotOrderClient exchange,
        ILocalOpenOrderStore store) => new(
            exchange,
            Options.Create(new WorkerRuntimeOptions
            {
                ExecutionMode = "TESTNET",
                NoTrade = false,
                PersistenceEnabled = true,
                ApplyMigrations = true,
                PostgresConnectionString = "Host=localhost",
                TestnetExecutionAcknowledged = true,
                TestnetOrderExecutionEnabled = true,
                RiskPolicyRequired = true,
                ReconciliationEnabled = true,
                AccountStreamEnabled = true
            }),
            Options.Create(new TradingOptions { Binance = new BinanceOptions { AllowedSymbols = "BTCUSDT" } }),
            store,
            new OrderProjectionService(store),
            new RecordingCancellationStore(store),
            NullLogger<TestnetCancelOutboxMessageExecutor>.Instance);

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

    private sealed class RecordingCancellationStore(ILocalOpenOrderStore openOrders) : IOrderCancellationStore
    {
        public Task<IReadOnlyList<CancelableExecutionSnapshot>> ReadExpiredCancellableExecutionsAsync(DateTimeOffset observedAtUtc, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CancelableExecutionSnapshot>>([]);

        public Task<CancelOutboxRecord> EnqueueCancellationAsync(CancelOutboxRecord record, CancellationToken cancellationToken = default) => Task.FromResult(record);

        public Task<IReadOnlyList<ExecutionReplanCandidate>> ReadReplanCandidatesAsync(int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExecutionReplanCandidate>>([]);

        public async Task<ExecutionReplanCandidate?> ResolveTerminalCancellationAsync(TerminalCancellationResolution resolution, CancellationToken cancellationToken = default)
        {
            var command = resolution.Command;
            var candidate = resolution.Resolution.ResidualQuantity.Value == 0m ? null : new ExecutionReplanCandidate(
                Guid.NewGuid(), command.OrderId, command.ClientOrderId, resolution.CancelMessageId, resolution.LifecycleEvent.EventId,
                command.Symbol, command.Side, command.Type, resolution.Resolution.ResidualQuantity, command.Attempt + 1,
                command.CorrelationId, resolution.LifecycleEvent.Transition.OccurredAtUtc);
            await openOrders.RemoveOpenOrderAsync(command.ClientOrderId.Value, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
    }

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

    private sealed class UnknownOutcomeTestnetClient : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            throw new BinanceOrderSubmissionAmbiguousException(request.ClientOrderId, new IOException("simulated response loss"));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            throw new BinanceApiException(System.Net.HttpStatusCode.ServiceUnavailable, "v3/order");
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTestnetClient(SubmitOrderCommand command) : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }
        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new BinanceSpotOrderResult(1, command.Symbol.Value, command.ClientOrderId.Value, "NEW", command.ReferencePrice.Value, command.Quantity.Value, 0m, 0));
        }
        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RejectedTestnetClient : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }

        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            throw new BinanceOrderRejectedException(
                request.ClientOrderId,
                new BinanceApiException(System.Net.HttpStatusCode.BadRequest, "v3/order"));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            throw new NotSupportedException();
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CancelRecordingTestnetClient(CancelOrderCommand command, string finalStatus, bool cancelFails = false, bool queryFails = false, string? queryStatus = null) : IBinanceSpotOrderClient
    {
        public int CancelCalls { get; private set; }
        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (cancelFails)
            {
                throw new IOException("simulated response loss");
            }

            return Task.FromResult(Result(finalStatus));
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            if (queryFails && QueryCalls > 1)
            {
                throw new IOException("simulated lookup failure");
            }

            return Task.FromResult(Result(QueryCalls == 1 ? queryStatus ?? finalStatus : finalStatus));
        }

        private BinanceSpotOrderResult Result(string status) => new(42, command.Symbol.Value, command.ClientOrderId.Value, status,
            command.LimitPrice!.Value.Value, command.OriginalQuantity.Value, status == "FILLED" ? command.OriginalQuantity.Value : command.FilledQuantity.Value,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private sealed class TestnetAccountClient(string baseFree = "1", string quoteFree = "1000", bool canTrade = true, bool canWithdraw = false) : IBinanceSpotAccountClient
    {
        public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BinanceAccountSnapshot(0, 0, 0, 0, canTrade, canWithdraw, true, 0, "SPOT",
                [new BinanceBalance("BTC", baseFree, "0"), new BinanceBalance("USDT", quoteFree, "0")]));

        public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol, BinanceTradeQuery? query = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PreTradeRejectedClient : IBinanceSpotOrderClient
    {
        public int SubmitCalls { get; private set; }

        public int QueryCalls { get; private set; }

        public Task<BinanceSpotOrderResult> SubmitAsync(BinanceSpotOrderRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            throw new BinancePreTradeValidationException(
                request.Symbol,
                [new("QUANTITY_STEP_MISMATCH", "Quantity does not align with step size.")]);
        }

        public Task<BinanceSpotOrderResult> QueryByClientOrderIdAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            throw new NotSupportedException();
        }

        public Task<BinanceSpotOrderResult> CancelAsync(string symbol, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOpenOrderStore : ILocalOpenOrderStore
    {
        public List<LocalOpenOrderRecord> Records { get; } = [];

        public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>(Records);

        public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.ClientOrderId == order.ClientOrderId);
            Records.Add(order);
            return Task.CompletedTask;
        }

        public Task RemoveOpenOrderAsync(string clientOrderId, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(item => item.ClientOrderId == clientOrderId);
            return Task.CompletedTask;
        }
    }
}
