namespace CryptoTrading.Backtesting.Autonomy;

/// <summary>
/// Calibrated sigmoid mapping parameters (John Platt, 1999) transforming
/// raw ensemble margin scores into monotonically calibrated posterior probabilities:
/// P(y = 1 | s) = 1 / (1 + exp(-(Alpha * s + Beta))).
/// </summary>
public readonly record struct PlattParameters(
    [property: System.Text.Json.Serialization.JsonRequired] decimal Alpha,
    [property: System.Text.Json.Serialization.JsonRequired] decimal Beta)
{
    public static readonly PlattParameters Default = new(1.0m, 0.0m);

    public decimal Transform(decimal rawScore)
    {
        var x = (double)((Alpha * rawScore) + Beta);
        if (x >= 35.0)
        {
            return 0.99m;
        }

        if (x <= -35.0)
        {
            return 0.01m;
        }

        var prob = (decimal)(1.0 / (1.0 + Math.Exp(-x)));
        return Math.Clamp(prob, 0.01m, 0.99m);
    }
}

/// <summary>
/// Deterministic Levenberg-Marquardt / Newton-Raphson regularized logistic calibrator.
/// </summary>
public static class PlattCalibrator
{
    public static PlattParameters Fit(
        IReadOnlyList<(decimal Score, bool Label)> samples,
        int maxIterations = 25,
        double minDelta = 1e-5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 6)
        {
            return PlattParameters.Default;
        }

        var posCount = 0;
        var negCount = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Label)
            {
                posCount++;
            }
            else
            {
                negCount++;
            }
        }

        if (posCount == 0 || negCount == 0)
        {
            return PlattParameters.Default;
        }

        // Regularized target probabilities (Platt 1999) preventing overfitting on small windows
        var tPos = (double)(posCount + 1) / (posCount + 2);
        var tNeg = 1.0 / (negCount + 2);

        var a = 1.0;
        var b = Math.Log((double)(posCount + 1) / (negCount + 1));
        const double ridge = 1e-4;

        for (var iter = 0; iter < maxIterations; iter++)
        {
            var gradA = 0.0;
            var gradB = 0.0;
            var h11 = ridge;
            var h22 = ridge;
            var h12 = 0.0;

            for (var i = 0; i < samples.Count; i++)
            {
                var s = (double)samples[i].Score;
                var t = samples[i].Label ? tPos : tNeg;
                var x = (a * s) + b;
                double p;
                if (x >= 35.0)
                {
                    p = 1.0 - 1e-15;
                }
                else if (x <= -35.0)
                {
                    p = 1e-15;
                }
                else
                {
                    p = 1.0 / (1.0 + Math.Exp(-x));
                }

                var d = p - t;
                gradA += d * s;
                gradB += d;

                var w = p * (1.0 - p);
                h11 += w * s * s;
                h22 += w;
                h12 += w * s;
            }

            var det = (h11 * h22) - (h12 * h12);
            if (Math.Abs(det) < 1e-12)
            {
                break;
            }

            var deltaA = -((h22 * gradA) - (h12 * gradB)) / det;
            var deltaB = -((h11 * gradB) - (h12 * gradA)) / det;

            var newA = a + deltaA;
            var newB = b + deltaB;

            // Enforce monotonicity: higher raw score must strictly yield equal or higher probability
            if (newA < 0.05)
            {
                newA = 0.05;
            }

            if (Math.Abs(newA - a) < minDelta && Math.Abs(newB - b) < minDelta)
            {
                a = newA;
                b = newB;
                break;
            }

            a = newA;
            b = newB;
        }

        var finalA = (decimal)Math.Clamp(a, 0.05, 20.0);
        var finalB = (decimal)Math.Clamp(b, -20.0, 20.0);
        return new PlattParameters(Math.Round(finalA, 4), Math.Round(finalB, 4));
    }
}
