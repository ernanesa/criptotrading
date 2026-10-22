using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoTrading.Backtesting.Autonomy;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class DynamicExitThresholdsTests
{
    [Fact]
    public void DefaultThresholds_HasExpectedFallbackValues()
    {
        var defaults = DynamicExitThresholds.Default;

        Assert.Equal(180.0m, defaults.TargetProfitBps);
        Assert.Equal(120.0m, defaults.ChandelierActivationBps);
        Assert.Equal(50.0m, defaults.ChandelierPullbackBps);
        Assert.Equal(40.0m, defaults.MinProfitLockBps);
        Assert.Equal(120.0m, defaults.StopLossBps);
    }

    [Fact]
    public void Calculate_LowVolatilityAsset_ScalesToMinimumFloors()
    {
        // 15 bps ATR (low-beta asset in tight consolidation)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 15.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(180.0m, thresholds.TargetProfitBps); // Floor at 180 bps
        Assert.Equal(75.0m, thresholds.ChandelierActivationBps); // roundTripFee + 60
        Assert.Equal(35.0m, thresholds.ChandelierPullbackBps); // Floor at 35 bps
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // Floor at 40 bps
        Assert.Equal(100.0m, thresholds.StopLossBps); // Floor at 100 bps
    }

    [Fact]
    public void Calculate_MediumVolatilityAsset_ExpandsTargetAndTrailingPullback()
    {
        // 60 bps ATR (typical active altcoin like SUI or DOGE)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 60.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(210.0m, thresholds.TargetProfitBps); // 60 * 3.5 = 210 bps
        Assert.Equal(120.0m, thresholds.ChandelierActivationBps); // 60 * 2.0 = 120 bps
        Assert.Equal(90.0m, thresholds.ChandelierPullbackBps); // 60 * 1.5 = 90 bps
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // 40 bps
        Assert.Equal(108.0m, thresholds.StopLossBps); // 60 * 1.8 = 108 bps
    }

    [Fact]
    public void Calculate_HighVolatilityAsset_ClampsAtCeilings()
    {
        // 160 bps ATR (extreme volatility breakout)
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 160.0m, roundTripFeeBps: 15.0m);

        Assert.Equal(450.0m, thresholds.TargetProfitBps); // Clamped to 450 bps ceiling
        Assert.Equal(320.0m, thresholds.ChandelierActivationBps); // 160 * 2.0 = 320 bps
        Assert.Equal(100.0m, thresholds.ChandelierPullbackBps); // Clamped to 100 bps ceiling
        Assert.Equal(40.0m, thresholds.MinProfitLockBps); // 40 bps
        Assert.Equal(250.0m, thresholds.StopLossBps); // Clamped to 250 bps ceiling
    }

    [Fact]
    public void Calculate_HigherCommissionRate_ExpandsActivationAndProfitLock()
    {
        // 30 bps round-trip fee
        var thresholds = DynamicExitThresholds.Calculate(effectiveVolBps: 20.0m, roundTripFeeBps: 30.0m);

        Assert.Equal(180.0m, thresholds.TargetProfitBps);
        Assert.Equal(90.0m, thresholds.ChandelierActivationBps); // 30 + 60 = 90 bps
        Assert.Equal(35.0m, thresholds.ChandelierPullbackBps);
        Assert.Equal(45.0m, thresholds.MinProfitLockBps); // 30 + 15 = 45 bps
        Assert.Equal(100.0m, thresholds.StopLossBps);
    }

    [Fact]
    public async Task AutonomyCycleHostedService_PopulatesDynamicThresholds_FromClosedBars()
    {
        var options = Options.Create(new WorkerRuntimeOptions
        {
            AutonomyEnabled = true,
            AutonomySymbol = "SUIUSDT",
            AutonomySymbols = "SUIUSDT",
            AutonomyInterval = "5m",
            AutonomyKlineLimit = 50,
            PollIntervalMilliseconds = 100,
            ExecutionMode = "TESTNET",
            NoTrade = true,
            ReconciliationAccount = "test-account",
            PersistenceEnabled = false,
            RiskPolicyRequired = false,
            ReconciliationEnabled = false,
            AccountStreamEnabled = false
        });

        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        var klines = new List<BinanceSpotKline>();
        // Generate 30 bars with ~50 bps ATR (swings of 2.0 on 200.0 price = 100 bps high-low)
        for (var i = 0; i < 30; i++)
        {
            var closeTime = baseTime.AddMinutes(i * 5);
            var openTime = closeTime.AddMinutes(-5);
            klines.Add(new BinanceSpotKline(
                openTime,
                Open: 200.0m,
                High: 202.0m,
                Low: 198.0m,
                Close: 200.5m,
                Volume: 1000m,
                closeTime,
                QuoteVolume: 200500m,
                Trades: 50,
                TakerBuyBaseVolume: 500m,
                TakerBuyQuoteVolume: 100250m));
        }

        var marketClient = new DynamicExitMarketClient(klines);
        var accountClient = new DynamicExitAccountClient();
        var policyStore = new FakePolicyStore();
        var riskContextStore = new FakeRiskContextStore();
        var decisionCommitStore = new InMemoryDecisionCommitStore();
        var operationalControl = new InMemoryOperationalControl(new AllowAllResetAuthorizer());
        using var supervisor = new RuntimeSupervisor(new InMemoryRuntimeTransitionSink(), TimeProvider.System, 0, RuntimeState.Ready);
        var cycle = new BaselineDecisionCycle(new CryptoTrading.Domain.Research.TrendFollowingParameters("test", "1.0.0", new Symbol("SUIUSDT"), 2, 3, 0m, 0m));

        using var hostedService = new AutonomyCycleHostedService(
            options,
            marketClient,
            accountClient,
            policyStore,
            riskContextStore,
            decisionCommitStore,
            operationalControl,
            supervisor,
            cycle,
            NullLogger<AutonomyCycleHostedService>.Instance);

        await hostedService.RunOnceAsync(options.Value, "SUIUSDT", CancellationToken.None);

        Assert.True(hostedService.DynamicExitThresholdsBySymbol.TryGetValue("SUIUSDT", out var thresholds));
        Assert.True(thresholds.TargetProfitBps >= 180.0m);
        Assert.True(thresholds.StopLossBps >= 100.0m);
        Assert.True(thresholds.ChandelierActivationBps >= 75.0m);
    }

    private sealed class DynamicExitMarketClient(IReadOnlyList<BinanceSpotKline> klines) : FakeMarketClient
    {
        public override Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(
            string symbol,
            string interval,
            DateTimeOffset? startTimeUtc = null,
            DateTimeOffset? endTimeUtc = null,
            int limit = 500,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(klines);
        }

        public override Task<BinanceBookTicker> GetBookTickerAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BinanceBookTicker(
                symbol,
                BidPrice: 200.0m,
                BidQuantity: 10m,
                AskPrice: 200.1m,
                AskQuantity: 10m,
                ObservedAtUtc: DateTimeOffset.UtcNow));
        }
    }

    private sealed class DynamicExitAccountClient : FakeAccountClient
    {
        public override Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
        {
            var balances = new List<BinanceBalanceSnapshot>
            {
                new("USDT", "5000.00", "0.00"),
                new("SUI", "0.00", "0.00")
            };
            return Task.FromResult(new BinanceAccountSnapshot(
                CanTrade: true,
                CanWithdraw: false,
                CanDeposit: false,
                UpdateTimeUtc: DateTimeOffset.UtcNow,
                Balances: balances));
        }

        public override Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol, CancellationToken cancellationToken = default)
        {
            var rates = new BinanceCommissionRates("0.00075", "0.00075", "0.00075", "0.00075");
            var discount = new BinanceCommissionDiscount("BNB", "0.25", EnabledForAccount: false, EnabledForSymbol: false);
            var response = new BinanceCommissionResponse(symbol, rates, rates, rates, discount);
            return Task.FromResult(new BinanceCommissionSnapshot(symbol, response, DateTimeOffset.UtcNow));
        }
    }
}
