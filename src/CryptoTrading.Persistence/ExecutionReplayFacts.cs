namespace CryptoTrading.Persistence;

internal sealed record ExecutionReplayFacts(
    long? TradeId,
    long OrderId,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    string Side,
    decimal Quantity,
    decimal Price,
    decimal Commission,
    string CommissionAsset,
    DateTimeOffset OccurredAtUtc,
    Guid LedgerEntryId);
