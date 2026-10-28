namespace SonicRoute.BenchmarkLab.Comparison;

public sealed class RoundEvidence
{
    public string Method { get; set; } = "";
    public int BaselineCount { get; set; }
    public int CandidateCount { get; set; }
    public double MeanDifference { get; set; }
    public double StandardError { get; set; }
    public double DegreesOfFreedom { get; set; }
    public double CriticalT { get; set; }
    public double Lower95 { get; set; }
    public double Upper95 { get; set; }
    public double DecisionLimit { get; set; }
    public string Notes { get; set; } = "Descriptive per-metric interval; assumes independent rounds and does not correct for multiple comparisons or establish causality.";
}

public static class RoundStatistics
{
    public static RoundEvidence Calculate(IReadOnlyList<double> a, IReadOnlyList<double> b, bool paired)
    {
        if (a.Count < 3 || b.Count < 3 || (paired && a.Count != b.Count)
            || a.Concat(b).Any(v => !double.IsFinite(v))) throw new ArgumentException("At least three valid rounds on each side are required; paired counts must match.");
        double mean = b.Average() - a.Average();
        double variance, df;
        if (paired)
        {
            var differences = a.Zip(b, (x, y) => y - x).ToArray();
            variance = SampleVariance(differences) / differences.Length;
            df = differences.Length - 1;
        }
        else
        {
            double va = SampleVariance(a) / a.Count, vb = SampleVariance(b) / b.Count;
            variance = va + vb;
            double denominator = va * va / (a.Count - 1) + vb * vb / (b.Count - 1);
            df = denominator > 0 ? variance * variance / denominator : Math.Min(a.Count, b.Count) - 1;
        }
        double t = Critical95(df), error = Math.Sqrt(variance);
        return new RoundEvidence { Method = paired ? "paired-t" : "welch-t", BaselineCount = a.Count, CandidateCount = b.Count,
            MeanDifference = mean, StandardError = error, DegreesOfFreedom = df, CriticalT = t,
            Lower95 = mean - t * error, Upper95 = mean + t * error };
    }

    private static double SampleVariance(IReadOnlyList<double> values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    // Rounded upward; floor df and cap at 30 to keep the table conservative.
    // NIST/SEMATECH t(.975) table; historical comparisons use Welch-Satterthwaite df.
    internal static double Critical95(double df)
    {
        double[] table = { 0, 12.707, 4.303, 3.183, 2.777, 2.571, 2.447, 2.365, 2.307, 2.263,
            2.229, 2.201, 2.179, 2.161, 2.145, 2.132, 2.120, 2.110, 2.102, 2.094, 2.086,
            2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.049, 2.046, 2.043 };
        return table[(int)Math.Clamp(Math.Floor(df), 1, 30)];
    }
}
