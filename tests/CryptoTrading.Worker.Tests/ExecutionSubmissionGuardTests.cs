#pragma warning disable CA2007 // Tests retain the xUnit synchronization context.
using CryptoTrading.Domain.Execution;
using CryptoTrading.Domain.Orders;
using CryptoTrading.Domain.Primitives;
using CryptoTrading.Domain.Risk;
using CryptoTrading.Domain.Runtime;
using CryptoTrading.Exchange.Binance;
using CryptoTrading.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoTrading.Worker.Tests;

public sealed class ExecutionSubmissionGuardTests
{
    [Theory]
    [InlineData(OperationalMode.SafeMode)]
    [InlineData(OperationalMode.KillSwitch)]
    public async Task OperationalStopBlocksBothGuardsBeforeAnyDependencyCall(OperationalMode mode)
    {
        var control = new InMemoryOperationalControl(new RejectReset());
        var stop = new OperationalControlCommand("test", "test stop", IdempotencyKey.New(), DateTimeOffset.UtcNow);
        if (mode == OperationalMode.KillSwitch) _ = await control.ActivateKillSwitchAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        else _ = await control.EnterSafeModeAsync(stop, cancellationToken: TestContext.Current.CancellationToken);
        await using var supervisor = new RuntimeSupervisor();
        var guard = Create(control, supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredIntentBlocksBeforeMarketOrAccountIo()
    {
        await using var supervisor = new RuntimeSupervisor();
        await supervisor.BeginBootstrappingAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkReconciliationAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkAccountStreamAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        await supervisor.MarkWarmupAsync(true, "test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(supervisor.Snapshot.CanCreateNewExposure);
        var guard = Create(new InMemoryOperationalControl(new RejectReset()), supervisor);
        var (command, message) = Command(DateTimeOffset.UtcNow.AddMinutes(-2));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(() => guard.ValidateFinalAsync(command, message, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ExecutionSubmissionGuard Create(IOperationalControl control, IRuntimeSupervisor supervisor) =>
        new(control, supervisor, null!, null!, null!, null!, null!, null!,
            Options.Create(new WorkerRuntimeOptions()), NullLogger<ExecutionSubmissionGuard>.Instance);

 [Fact]
 public async Task FinalGuardBlocksAdverseFreshCommission()
 {
  await using var supervisor=await ReadySupervisorAsync(); var account=new GuardAccountClient { CommissionRate="0.01" };
  var (command,message)=FreshCommand();
  var exception=await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(()=>ReadyGuard(supervisor,new GuardMarketClient(),account).ValidateFinalAsync(command,message,TestContext.Current.CancellationToken));
  Assert.Equal("FINAL_COMMISSION_RATE_ADVERSE_OR_ELIGIBILITY_UNPROVEN",exception.Message);
 }
 [Fact]
 public async Task FinalGuardBlocksWhenFreshCommissionQueryFails()
 {
  await using var supervisor=await ReadySupervisorAsync(); var (command,message)=FreshCommand();
  var exception=await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(()=>ReadyGuard(supervisor,new GuardMarketClient(),new GuardAccountClient { ThrowOnCommission=true }).ValidateFinalAsync(command,message,TestContext.Current.CancellationToken));
  Assert.Equal("FINAL_REVALIDATION_UNAVAILABLE",exception.Message);
 }
 [Fact]
 public async Task FinalGuardBlocksAdverseAccountPosition()
 {
  await using var supervisor=await ReadySupervisorAsync(); var (command,message)=FreshCommand();
  var exception=await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(()=>ReadyGuard(supervisor,new GuardMarketClient(),new GuardAccountClient { BitcoinFree="100" }).ValidateFinalAsync(command,message,TestContext.Current.CancellationToken));
  Assert.Equal("PRE_SUBMIT_ACCOUNTING_OR_LOSS_BREAKER",exception.Message);
 }
 [Fact]
 public async Task FinalGuardBlocksAdverseFreshMarketPrice()
 {
  await using var supervisor=await ReadySupervisorAsync(); var (command,message)=FreshCommand();
  var exception=await Assert.ThrowsAsync<ExecutionDispatchBlockedException>(()=>ReadyGuard(supervisor,new GuardMarketClient { AskPrice=20000m },new GuardAccountClient()).ValidateFinalAsync(command,message,TestContext.Current.CancellationToken));
  Assert.Equal("PRE_SUBMIT_MARKET_OR_NOTIONAL_CHANGED",exception.Message);
 }
 [Fact]
 public async Task FinalGuardAcceptsConservativeCommissionAndRefetchesMarket()
 {
  await using var supervisor=await ReadySupervisorAsync(); var market=new GuardMarketClient(); var (command,message)=FreshCommand();
  var guard=ReadyGuard(supervisor,market,new GuardAccountClient());
  await guard.ValidateAsync(command,message,TestContext.Current.CancellationToken);
  await guard.ValidateFinalAsync(command,message,TestContext.Current.CancellationToken);
  Assert.Equal(2,market.BookTickerCalls);
 }
 [Fact]
 public async Task FinalGuardAcceptsPlannedMaximumMakerRateWhenItExceedsTakerRate()
 {
  await using var supervisor=await ReadySupervisorAsync(); var market=new GuardMarketClient();
  var account=new GuardAccountClient { CommissionRate="0", MakerRate="0.004", TakerRate="0.002" };
  var (original,message)=FreshCommand(); var command=original with { EstimatedCommissionRate=.004m };
  await ReadyGuard(supervisor,market,account).ValidateFinalAsync(command,message,TestContext.Current.CancellationToken);
 }
 private static async Task<RuntimeSupervisor> ReadySupervisorAsync()
 {
  var supervisor=new RuntimeSupervisor(); var cancellationToken=TestContext.Current.CancellationToken;
  await supervisor.BeginBootstrappingAsync("test",cancellationToken); await supervisor.MarkReconciliationAsync(true,"test",cancellationToken);
  await supervisor.MarkAccountStreamAsync(true,"test",cancellationToken); await supervisor.MarkWarmupAsync(true,"test",cancellationToken);
  return supervisor;
 }
 private static ExecutionSubmissionGuard ReadyGuard(RuntimeSupervisor supervisor,GuardMarketClient market,GuardAccountClient account) =>
  new(new InMemoryOperationalControl(new RejectReset()),supervisor,new GuardPolicyStore(),new GuardRiskContextStore(),new GuardOpenOrderStore(),new GuardSubmissionStore(),market,account,Options.Create(new WorkerRuntimeOptions()),NullLogger<ExecutionSubmissionGuard>.Instance);
 private static (SubmitOrderCommand Command,LeasedOutboxMessage Message) FreshCommand()
 {
  var now=DateTimeOffset.UtcNow; var correlation=CorrelationId.New(); var idempotency=IdempotencyKey.New();
  var command=new SubmitOrderCommand(OrderId.New(),ClientOrderId.New(),new Symbol("BTCUSDT"),OrderSide.Buy,OrderType.Market,new Quantity(.01m),new Price(100m),100m,correlation,idempotency,"v1",now,.002m,"testhash","test:commission",now,now.AddMinutes(1));
  return (command,new LeasedOutboxMessage(Guid.NewGuid(),OrderExecutionPipeline.SubmitOrderMessageType,correlation,idempotency,"{}",now,1,now.AddMinutes(1),"test"));
 }
 private static BinanceSpotKline Kline(DateTimeOffset open,decimal close) =>
  new(open,open.AddMinutes(1).AddMilliseconds(-1),close,close,close,close,1m,"1m");
 private sealed class GuardMarketClient : IBinanceSpotMarketClient
 {
 public int BookTickerCalls { get; private set; }
  public decimal AskPrice { get; init; }=101m;
  public Task PingAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
  public Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new BinanceServerTime(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
  public Task<IReadOnlyList<BinanceSpotKline>> GetKlinesAsync(string symbol,string interval,DateTimeOffset? startTimeUtc=null,DateTimeOffset? endTimeUtc=null,int limit=500,CancellationToken cancellationToken=default)
  {
   var now=DateTimeOffset.UtcNow; var minute=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,now.Minute,0,TimeSpan.Zero);
   return Task.FromResult<IReadOnlyList<BinanceSpotKline>>([Kline(minute.AddMinutes(-4),100m),Kline(minute.AddMinutes(-3),101m),Kline(minute.AddMinutes(-2),102m),Kline(minute.AddMinutes(-1),103m),Kline(minute,104m)]);
  }
  public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol,DateTimeOffset? endTimeUtc=null,int limit=500,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BinanceAggregateTrade>>([]);
  public Task<BinanceBookTicker> GetBookTickerAsync(string symbol,CancellationToken cancellationToken=default){ BookTickerCalls++; return Task.FromResult(new BinanceBookTicker(symbol,100m,10m,AskPrice,10m,DateTimeOffset.UtcNow)); }
  public Task<BinanceExchangeInfo> GetExchangeInfoAsync(string? symbol=null,CancellationToken cancellationToken=default)=>throw new NotImplementedException();
  public Task<BinanceSymbolTradingRules> GetTradingRulesAsync(string symbol,CancellationToken cancellationToken=default)=>throw new NotImplementedException();
 }
 private sealed class GuardAccountClient : IBinanceSpotAccountClient
 {
 public string CommissionRate { get; init; }="0.001";
  public string? MakerRate { get; init; }
  public string? TakerRate { get; init; }
  public string BitcoinFree { get; init; }="0";
  public bool ThrowOnCommission { get; init; }
  public Task<BinanceAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new BinanceAccountSnapshot(0,0,0,0,true,false,true,0,"SPOT",[new BinanceBalance("BTC",BitcoinFree,"0"),new BinanceBalance("USDT","1000","0")]));
  public Task<BinanceCommissionSnapshot> GetCommissionAsync(string symbol,CancellationToken cancellationToken=default)
  {
   if(ThrowOnCommission) throw new InvalidOperationException("query failed");
   return Task.FromResult(new BinanceCommissionSnapshot(new BinanceCommissionResponse(symbol,new BinanceCommissionRateSet(MakerRate??CommissionRate,TakerRate??CommissionRate,CommissionRate,CommissionRate),new BinanceCommissionRateSet("0","0","0","0"),new BinanceCommissionRateSet("0","0","0","0"),new BinanceCommissionDiscount(false,false,null,null)),DateTimeOffset.UtcNow));
  }
  public Task<IReadOnlyList<BinanceOpenOrder>> GetOpenOrdersAsync(string? symbol=null,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BinanceOpenOrder>>([]);
  public Task<IReadOnlyList<BinanceTrade>> GetMyTradesAsync(string symbol,BinanceTradeQuery? query=null,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<BinanceTrade>>([]);
  public Task<BinanceClockSnapshot> SynchronizeClockAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new BinanceClockSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),TimeSpan.Zero,true));
 }
 private sealed class GuardPolicyStore : IRiskPolicyStore
 {
  private readonly RiskPolicyDocument document;
  public GuardPolicyStore(){ var policy=new RiskPolicy("v1",1000m,5000m,500m,1000m,TimeSpan.FromMinutes(15),withdrawalsEnabled:false); document=RiskPolicyDocument.CreateDraft(policy,DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(10),"creator").Approve("app","approver",DateTimeOffset.UtcNow.AddDays(-1)).Activate(DateTimeOffset.UtcNow.AddDays(-1)); }
  public Task<RiskPolicyDocument?> GetActiveRiskPolicyAsync(DateTimeOffset atUtc,CancellationToken cancellationToken=default)=>Task.FromResult<RiskPolicyDocument?>(document);
  public Task SaveRiskPolicyAsync(RiskPolicyDocument document,CancellationToken cancellationToken=default)=>Task.CompletedTask;
  public Task<RiskPolicyDocument> ApproveRiskPolicyAsync(string policyVersion,string approvalId,string approver,DateTimeOffset approvedAtUtc,CancellationToken cancellationToken=default)=>Task.FromResult(document);
  public Task<RiskPolicyDocument> ActivateRiskPolicyAsync(string policyVersion,DateTimeOffset activatedAtUtc,CancellationToken cancellationToken=default)=>Task.FromResult(document);
 }
 private sealed class GuardRiskContextStore : IAutonomyRiskContextStore
 {
  public Task<AutonomyRiskContextSnapshot> ReadAutonomyRiskContextAsync(Symbol symbol,Asset quoteAsset,DateTimeOffset dayStartUtc,CancellationToken cancellationToken=default)=>Task.FromResult(new AutonomyRiskContextSnapshot(0m,0m,0m,true,DateTimeOffset.UtcNow,1000m,1000m,0m,true,true));
  public Task<AutonomyRiskContextSnapshot> RecordAutonomyRiskContextAsync(string scope,Symbol symbol,Asset quoteAsset,DateTimeOffset dayStartUtc,decimal? currentEquityNotional,DateTimeOffset valuationAtUtc,CancellationToken cancellationToken=default)=>Task.FromResult(new AutonomyRiskContextSnapshot(0m,0m,0m,true,valuationAtUtc,1000m,1000m,0m,true,true));
 }
 private sealed class GuardOpenOrderStore : ILocalOpenOrderStore
 {
  public Task<IReadOnlyList<LocalOpenOrderRecord>> ReadOpenOrdersAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<LocalOpenOrderRecord>>([]);
  public Task UpsertOpenOrderAsync(LocalOpenOrderRecord order,CancellationToken cancellationToken=default)=>Task.CompletedTask;
  public Task RemoveOpenOrderAsync(string clientOrderId,CancellationToken cancellationToken=default)=>Task.CompletedTask;
 }
 private sealed class GuardSubmissionStore : IDurableSubmissionStore
 {
  public Task<bool> HasSubmissionStartedAsync(LeasedOutboxMessage message,CancellationToken cancellationToken=default)=>Task.FromResult(false);
  public Task<bool> TryBeginSubmissionAsync(LeasedOutboxMessage message,TimeSpan minimumLeaseRemaining,CancellationToken cancellationToken=default)=>Task.FromResult(true);
  public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message,CancellationToken cancellationToken=default)=>Task.FromResult(true);
  public Task<bool> ValidateOutboxLeaseAsync(LeasedOutboxMessage message,TimeSpan minimumLeaseRemaining,CancellationToken cancellationToken=default)=>Task.FromResult(true);
 }
 private sealed class RejectReset : IOperationalControlResetAuthorizer
    {
        public bool IsAuthorized(OperationalControlSnapshot current, OperationalControlCommand command,
            OperationalControlResetAuthorization authorization) => false;
    }

    private static (SubmitOrderCommand Command, LeasedOutboxMessage Message) Command(DateTimeOffset created)
    {
        var correlation = CorrelationId.New();
        var idempotency = IdempotencyKey.New();
        var command = new SubmitOrderCommand(OrderId.New(), ClientOrderId.New(), new Symbol("BTCUSDT"),
            OrderSide.Buy, OrderType.Market, new Quantity(0.01m), new Price(10000m), 100m,
            correlation, idempotency, "risk-v1", created);
        return (command, new LeasedOutboxMessage(Guid.NewGuid(), OrderExecutionPipeline.SubmitOrderMessageType,
            correlation, idempotency, "{}", created, 1, created.AddMinutes(1), "test"));
    }
}
