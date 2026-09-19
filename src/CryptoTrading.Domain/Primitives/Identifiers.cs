namespace CryptoTrading.Domain.Primitives;

public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.NewGuid());

    public static OrderId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out OrderId result)
    {
        var success = Guid.TryParse(value, out var parsed);
        result = new OrderId(parsed);
        return success;
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct LedgerEntryId(Guid Value)
{
    public static LedgerEntryId New() => new(Guid.NewGuid());

    public static LedgerEntryId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out LedgerEntryId result)
    {
        var success = Guid.TryParse(value, out var parsed);
        result = new LedgerEntryId(parsed);
        return success;
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct StrategyId(Guid Value)
{
    public static StrategyId New() => new(Guid.NewGuid());

    public static StrategyId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out StrategyId result)
    {
        var success = Guid.TryParse(value, out var parsed);
        result = new StrategyId(parsed);
        return success;
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ExperimentId(Guid Value)
{
    public static ExperimentId New() => new(Guid.NewGuid());

    public static ExperimentId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out ExperimentId result)
    {
        var success = Guid.TryParse(value, out var parsed);
        result = new ExperimentId(parsed);
        return success;
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct CorrelationId(Guid Value)
{
    public static CorrelationId New() => new(Guid.NewGuid());

    public static CorrelationId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out CorrelationId result)
    {
        var success = Guid.TryParse(value, out var parsed);
        result = new CorrelationId(parsed);
        return success;
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ClientOrderId
{
    public const int MaxLength = 36;

    public ClientOrderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Client order IDs cannot exceed {MaxLength} characters.");
        }

        if (!value.All(IsAllowedCharacter))
        {
            throw new ArgumentException("Client order IDs may contain only ASCII letters, digits, '-' or '_'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static ClientOrderId New() => new($"ct-{Guid.NewGuid():N}");

    public static implicit operator string(ClientOrderId value) => value.Value;

    public override string ToString() => Value;

    private static bool IsAllowedCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';
}

public readonly record struct IdempotencyKey
{
    public const int MaxLength = 128;

    public IdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Idempotency keys cannot exceed {MaxLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Idempotency keys cannot contain control characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static IdempotencyKey New() => new($"ct-{Guid.NewGuid():N}");

    public static implicit operator string(IdempotencyKey value) => value.Value;

    public override string ToString() => Value;
}
