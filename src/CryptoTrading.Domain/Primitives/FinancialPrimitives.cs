using System.Text.Json.Serialization;

namespace CryptoTrading.Domain.Primitives;

public readonly record struct Asset
{
    [JsonConstructor]
    public Asset(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length is < 2 or > 20 || !normalized.All(IsAllowedCharacter))
        {
            throw new ArgumentException("An asset must contain 2-20 ASCII letters or digits.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public static Asset FromString(string value) => new(value);

    public static implicit operator Asset(string value) => new(value);

    public override string ToString() => Value;

    private static bool IsAllowedCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9';
}

public readonly record struct Symbol
{
    [JsonConstructor]
    public Symbol(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length is < 2 or > 32 || !normalized.All(IsAllowedCharacter))
        {
            throw new ArgumentException("A symbol must contain 2-32 ASCII letters, digits or '_'.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public static Symbol FromString(string value) => new(value);

    public static implicit operator Symbol(string value) => new(value);

    public override string ToString() => Value;

    private static bool IsAllowedCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}

public readonly record struct Quantity
{
    public Quantity(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Quantity cannot be negative.");
        }

        Value = value;
    }

    public decimal Value { get; }

    public static Quantity FromDecimal(decimal value) => new(value);

    public static implicit operator Quantity(decimal value) => new(value);

    public static Quantity Add(Quantity left, Quantity right) => left + right;

    public static Quantity Subtract(Quantity left, Quantity right) => left - right;

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + right.Value);

    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - right.Value);

    public override string ToString() => Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct Price
{
    public Price(decimal value)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Price must be greater than zero.");
        }

        Value = value;
    }

    public decimal Value { get; }

    public static Price FromDecimal(decimal value) => new(value);

    public static implicit operator Price(decimal value) => new(value);

    public override string ToString() => Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct Money
{
    public Money(decimal amount, Asset asset)
    {
        Amount = amount;
        Asset = asset;
    }

    public decimal Amount { get; }

    public Asset Asset { get; }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameAsset(left, right);
        return new(left.Amount + right.Amount, left.Asset);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameAsset(left, right);
        return new(left.Amount - right.Amount, left.Asset);
    }

    public static Money Add(Money left, Money right) => left + right;

    public static Money Subtract(Money left, Money right) => left - right;

    public override string ToString() =>
        $"{Amount.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)} {Asset}";

    private static void EnsureSameAsset(Money left, Money right)
    {
        if (left.Asset != right.Asset)
        {
            throw new InvalidOperationException("Money operations require the same asset.");
        }
    }
}

public readonly record struct Percentage
{
    public Percentage(decimal value)
    {
        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Percentage must be between 0 and 1.");
        }

        Value = value;
    }

    public decimal Value { get; }

    public static Percentage FromDecimal(decimal value) => new(value);

    public static implicit operator Percentage(decimal value) => new(value);

    public override string ToString() => Value.ToString("P2", System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct RiskAmount
{
    public RiskAmount(decimal amount, Asset asset)
    {
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Risk amount cannot be negative.");
        }

        Value = new Money(amount, asset);
    }

    public Money Value { get; }
}
