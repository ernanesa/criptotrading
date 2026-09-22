namespace CryptoTrading.Domain.Research;

/// <summary>Immutable configuration envelope constraining permissible values and step rates for a strategy parameter.</summary>
public sealed record ParameterEnvelope(
    string ParameterName,
    decimal MinValue,
    decimal MaxValue,
    decimal MaxStepChangeRate);

/// <summary>Result of evaluating parameter adaptation against its envelope.</summary>
public sealed record ParameterAdaptationResult(
    string ParameterName,
    decimal CurrentValue,
    decimal TargetValue,
    decimal AdaptedValue,
    bool IsAccepted,
    string Reason);

/// <summary>
/// Bounded parameter adaptation engine (US-176).
/// Ensures automated continuous learning cannot drift strategy parameters beyond pre-authorized safety envelopes.
/// </summary>
public static class BoundedParameterAdaptor
{
    public static ParameterAdaptationResult Adapt(
        decimal currentValue,
        decimal proposedValue,
        ParameterEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.MinValue >= envelope.MaxValue)
        {
            throw new ArgumentException("Envelope MinValue must be strictly less than MaxValue.", nameof(envelope));
        }

        if (envelope.MaxStepChangeRate <= 0m)
        {
            throw new ArgumentException("Envelope MaxStepChangeRate must be strictly positive.", nameof(envelope));
        }

        // Clamp proposal to absolute range
        var clampedToRange = Math.Clamp(proposedValue, envelope.MinValue, envelope.MaxValue);

        // Maximum allowed deviation in a single adaptation cycle
        var maxDelta = Math.Abs(currentValue) * envelope.MaxStepChangeRate;
        if (maxDelta == 0m)
        {
            maxDelta = (envelope.MaxValue - envelope.MinValue) * envelope.MaxStepChangeRate;
        }

        var delta = clampedToRange - currentValue;
        var stepDirection = Math.Sign(delta);
        var stepMagnitude = Math.Min(Math.Abs(delta), maxDelta);

        var finalValue = currentValue + (stepDirection * stepMagnitude);
        finalValue = Math.Clamp(finalValue, envelope.MinValue, envelope.MaxValue);

        var isWithinEnvelope = proposedValue >= envelope.MinValue && proposedValue <= envelope.MaxValue;
        var isConstrainedByStep = Math.Abs(delta) > maxDelta;

        var reason = !isWithinEnvelope
            ? $"VALUE_CLAMPED: Proposed value {proposedValue:G4} was outside envelope [{envelope.MinValue:G4}, {envelope.MaxValue:G4}]."
            : isConstrainedByStep
                ? $"STEP_CONSTRAINED: Change limited by max step rate {envelope.MaxStepChangeRate:P0} to {finalValue:G4}."
                : "ACCEPTED: Adaptation within normal envelope and step parameters.";

        return new ParameterAdaptationResult(
            envelope.ParameterName,
            currentValue,
            proposedValue,
            Math.Round(finalValue, 4),
            IsAccepted: true,
            reason);
    }
}
