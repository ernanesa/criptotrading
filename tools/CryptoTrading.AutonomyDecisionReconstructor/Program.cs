#pragma warning disable CA1031 // CLI diagnostic tool catches top-level exceptions and returns safe non-zero exit codes.
#pragma warning disable CA1303 // Operator-facing CLI messages and templates are intentional.
#pragma warning disable CA2007 // CLI tool runs in standard console context.
#pragma warning disable CA1849 // Console synchronous writes are acceptable in simple CLI driver.

using System.Globalization;
using System.Text.Json;
using CryptoTrading.Persistence;

string? decisionId = null;
string? connectionString = null;
var jsonOutput = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if ((arg is "--decision-id" or "-d") && i + 1 < args.Length)
    {
        decisionId = args[++i];
    }
    else if ((arg is "--connection-string" or "-c") && i + 1 < args.Length)
    {
        connectionString = args[++i];
    }
    else if (arg is "--json")
    {
        jsonOutput = true;
    }
    else if (arg is "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }
}

if (string.IsNullOrWhiteSpace(decisionId))
{
    Console.Error.WriteLine("Error: --decision-id <id> is required.");
    PrintUsage();
    return 1;
}

connectionString ??= Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Error: Database connection string is required via --connection-string or POSTGRES_CONNECTION_STRING.");
    return 1;
}

try
{
    await using var store = new PostgresTradingStore(new PostgresPersistenceOptions(connectionString));
    var report = await AutonomyDecisionReconstructor.ReconstructAsync(
        decisionId.Trim(),
        store,
        store,
        store,
        store).ConfigureAwait(false);

    if (report is null)
    {
        Console.Error.WriteLine($"Error: Decision '{decisionId}' was not found in durable autonomy storage.");
        return 2;
    }

    if (jsonOutput)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        Console.WriteLine(json);
    }
    else
    {
        PrintHumanReport(report);
    }

    return report.CanonicalIdentityHashMatches ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal reconstruction failure: {ex.Message}");
    return 4;
}

static void PrintUsage()
{
    Console.WriteLine("""
    Usage: CryptoTrading.AutonomyDecisionReconstructor [options]

    Options:
      -d, --decision-id <id>         Canonical decision identifier to reconstruct (required)
      -c, --connection-string <conn> PostgreSQL connection string (defaults to POSTGRES_CONNECTION_STRING env var)
      --json                         Output report as formatted JSON
      -h, --help                     Show this help message
    """);
}

static void PrintHumanReport(AutonomyDecisionReconstructionReport report)
{
    var commit = report.Commit;
    Console.WriteLine("================================================================================");
    Console.WriteLine($" AUTONOMY DECISION AUDIT RECONSTRUCTION: {commit.DecisionId}");
    Console.WriteLine("================================================================================");
    Console.WriteLine($"Environment:             {commit.Environment}");
    Console.WriteLine($"Account Scope:           {commit.AccountScope}");
    Console.WriteLine($"Checkpoint Scope:        {commit.CheckpointScope}");
    Console.WriteLine($"Symbol:                  {commit.Symbol.Value}");
    Console.WriteLine($"Strategy:                {commit.StrategyId} (v{commit.StrategyVersion})");
    Console.WriteLine($"Close Time (UTC):        {commit.CloseTimeUtc:O}");
    Console.WriteLine($"Available At (UTC):      {commit.AvailableAtUtc:O}");
    Console.WriteLine($"Feature Schema:          {commit.FeatureSchemaVersion}");
    Console.WriteLine($"Feature Fingerprint:     {commit.FeatureFingerprint}");
    Console.WriteLine($"Config Fingerprint:      {commit.FeatureConfigurationFingerprint}");
    Console.WriteLine($"Signal:                  {commit.Signal}");
    Console.WriteLine($"Strategy Reason:         {commit.StrategyReason}");
    Console.WriteLine($"Outcome:                 {commit.Outcome}");
    Console.WriteLine($"No-Trade / Block Reason: {commit.NoTradeReason ?? "N/A"}");
    Console.WriteLine($"Audit Identity Check:    {(report.CanonicalIdentityHashMatches ? "PASSED (Deterministic SHA-256 match)" : "FAILED (Identity hash mismatch!)")}");

    if (commit.RiskDecision is { } risk)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" RISK EVALUATION VERDICT");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Status:                  {risk.Status}");
        Console.WriteLine($"Policy Version:          {risk.PolicyVersion}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Requested Notional:      {risk.RequestedTargetNotional:F4}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Authorized Notional:     {risk.AuthorizedTargetNotional:F4}"));
        Console.WriteLine($"Reason Codes:            {string.Join(", ", risk.ReasonCodes)}");
    }

    if (report.SubmissionContext is { } ctx)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" OMS EXECUTION SUBMISSION CONTEXT");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"Client Order ID:         {ctx.ClientOrderId}");
        Console.WriteLine($"Side:                    {ctx.Side}");
        Console.WriteLine($"Order Type:              {ctx.Type}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Authorized Quantity:     {ctx.AuthorizedQuantity.Value:F8}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Reference Price:         {ctx.ReferencePrice.Value:F4}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Authorized Notional:     {ctx.AuthorizedNotional:F4}"));
        Console.WriteLine($"Execution Attempt:       {ctx.ExecutionAttempt}");
        Console.WriteLine($"Planned At (UTC):        {ctx.PlannedAtUtc:O}");
    }

    if (report.ExpectedFee is { } fee)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" EXECUTION FEE PROJECTION");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Fee Rate:                {fee.Rate:P4}"));
        Console.WriteLine($"Snapshot Hash:           {fee.SnapshotHash}");
        Console.WriteLine($"Commission Source:       {fee.Source}");
        Console.WriteLine($"Observed At (UTC):       {fee.ObservedAtUtc:O}");
    }

    if (report.LifecycleEvents is { Count: > 0 } events)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine(" ORDER LIFECYCLE TRANSITIONS");
        Console.WriteLine("--------------------------------------------------------------------------------");
        foreach (var evt in events)
        {
            Console.WriteLine($"[{evt.Transition.OccurredAtUtc:O}] {evt.Transition.From} -> {evt.Transition.To} (IdempotencyKey={evt.Transition.IdempotencyKey.Value}, ExchangeOrderId={evt.Transition.ExchangeOrderId ?? "none"})");
        }
    }

    Console.WriteLine("================================================================================");
}
