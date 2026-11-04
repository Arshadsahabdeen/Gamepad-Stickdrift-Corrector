namespace StickFix.Core;

[Flags]
public enum StickFinding
{
    None = 0,
    ConsistentOffset = 1,
    Noisy = 2,
    UnstableDrift = 4,
    DoesNotReturn = 8,
    RestrictedRange = 16,
    PossibleDamage = 32,
    MovedDuringTest = 64,
}

public sealed record AxisStats(
    double Mean, double Median, double Min, double Max, double StdDev, double Jitter, double DriftPerSecond, double SegmentSpread)
{
    public double Span => Max - Min;
}

public sealed record RestRun(AxisStats X, AxisStats Y, double Offset, double R99, bool Moved);

public sealed record AxisRange(double Min, double Max);

public enum Confidence { Low, Medium, High }

public enum CorrectionStrength { Minimal, Balanced, Strong }

public sealed record Recommendation(
    StickSettings Settings, StickFinding Findings, Confidence Confidence, bool AutoApplyAllowed, IReadOnlyList<string> Warnings);

/// <summary>RestBefore/RestAfter: largest distance from zero at rest. MinReach: weakest of the four directions.</summary>
public sealed record CorrectionCheck(double RestBefore, double RestAfter, double MinReach);

public static class Diagnosis
{
    // Starting values, not yet tuned on real hardware. All are in normalized units (full travel = 1.0).
    public const double MoveSpan = 0.15, OffsetLimit = 0.03, NoiseLimit = 0.01, WanderLimit = 0.02;
    public const double ReturnLimit = 0.05, DamageOffset = 0.35, MinHalfRange = 0.70;
    public const double WarnDeadzone = 0.12, MaxDeadzone = 0.25;
    public const int MinSamples = 50;

    public static RestRun Analyze(IReadOnlyList<(double X, double Y)> samples, double sampleRateHz)
    {
        if (samples.Count < MinSamples)
            throw new ArgumentException($"Need at least {MinSamples} samples, got {samples.Count}.", nameof(samples));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        var xs = samples.Select(s => s.X).ToArray();
        var ys = samples.Select(s => s.Y).ToArray();
        var x = Stats(xs, sampleRateHz);
        var y = Stats(ys, sampleRateHz);
        var distances = samples.Select(s => Hypot(s.X - x.Median, s.Y - y.Median)).OrderBy(d => d).ToArray();
        return new RestRun(x, y, Hypot(x.Median, y.Median), distances[(int)((distances.Length - 1) * 0.99)],
            x.Span > MoveSpan || y.Span > MoveSpan);
    }

    /// <summary>
    /// Classifies one or more rest runs. Runs should be separated by moving and releasing the stick, which is
    /// what makes the return-to-center check meaningful. A single moved run could just be a bumped desk, so
    /// it takes two in a row to call it unstable.
    /// </summary>
    public static StickFinding Classify(IReadOnlyList<RestRun> runs, AxisRange? rangeX = null, AxisRange? rangeY = null)
    {
        if (runs.Count == 0) throw new ArgumentException("Need at least one run.", nameof(runs));
        var last = runs[^1];

        if (last.Moved)
            return runs.Count >= 2 && runs[^2].Moved
                ? StickFinding.UnstableDrift | StickFinding.PossibleDamage
                : StickFinding.MovedDuringTest;

        var findings = StickFinding.None;
        if (ReturnScatter(runs) > ReturnLimit) findings |= StickFinding.DoesNotReturn;
        if (Wander(last.X) > WanderLimit || Wander(last.Y) > WanderLimit) findings |= StickFinding.UnstableDrift;
        if (Math.Max(last.X.StdDev, last.Y.StdDev) > NoiseLimit) findings |= StickFinding.Noisy;
        if (last.Offset > OffsetLimit) findings |= StickFinding.ConsistentOffset;
        if (last.Offset > DamageOffset) findings |= StickFinding.PossibleDamage;
        if (rangeX is not null && Restricted(rangeX, last.X.Median)) findings |= StickFinding.RestrictedRange;
        if (rangeY is not null && Restricted(rangeY, last.Y.Median)) findings |= StickFinding.RestrictedRange;
        return findings;
    }

    public static Recommendation Recommend(
        IReadOnlyList<RestRun> runs, AxisRange rangeX, AxisRange rangeY, CorrectionStrength strength)
    {
        var findings = Classify(runs, rangeX, rangeY);
        var last = runs[^1];
        var warnings = new List<string>();

        var k = strength switch { CorrectionStrength.Minimal => 1.2, CorrectionStrength.Strong => 2.5, _ => 1.5 };
        var wanted = k * (last.R99 + ReturnScatter(runs));
        // Floor from a real PowerA: a noise-free stick still came to rest about 2% away after being moved.
        var deadzone = Math.Clamp(wanted, Math.Min(0.02 * k, MaxDeadzone), MaxDeadzone);
        if (wanted > MaxDeadzone)
            warnings.Add($"This stick would need a {wanted:P0} deadzone, more than the {MaxDeadzone:P0} limit. Fine aiming would suffer.");
        else if (wanted > WarnDeadzone)
            warnings.Add($"This stick needs a {wanted:P0} deadzone, large enough to reduce fine aiming precision.");

        // Average every rest position, so a stick that settles differently each time ends up nearest the middle.
        var (x, xOk) = BuildAxis(runs.Average(r => r.X.Median), rangeX);
        var (y, yOk) = BuildAxis(runs.Average(r => r.Y.Median), rangeY);
        if (!xOk || !yOk) warnings.Add("Stick range measurement was too small, so default range was used. Repeat the sweep at full tilt.");

        if (findings.HasFlag(StickFinding.MovedDuringTest)) warnings.Add("The stick moved during measurement. Repeat without touching it.");
        if (findings.HasFlag(StickFinding.UnstableDrift)) warnings.Add("Resting position wanders over time. A center offset can't fix this reliably.");
        if (findings.HasFlag(StickFinding.DoesNotReturn)) warnings.Add("The stick doesn't return to the same center after moving. This usually means mechanical wear.");
        if (findings.HasFlag(StickFinding.PossibleDamage)) warnings.Add("Readings suggest physical damage. Software correction may not be enough; consider repair or replacement.");
        // A sweep too small to measure means the stick wasn't rotated, not that it's damaged.
        if (findings.HasFlag(StickFinding.RestrictedRange) && xOk && yOk) warnings.Add("Travel is restricted in at least one direction. Check for debris or damage.");

        const StickFinding blocking = StickFinding.MovedDuringTest | StickFinding.UnstableDrift
            | StickFinding.DoesNotReturn | StickFinding.PossibleDamage;
        var allowed = (findings & blocking) == 0 && wanted <= MaxDeadzone && xOk && yOk;

        var confidence = !allowed ? Confidence.Low
            : runs.Count >= 2 && !findings.HasFlag(StickFinding.Noisy) && !findings.HasFlag(StickFinding.RestrictedRange)
                ? Confidence.High : Confidence.Medium;

        return new Recommendation(
            new StickSettings { X = x, Y = y, DeadzoneInner = deadzone, Hysteresis = deadzone * 0.25 },
            findings, confidence, allowed, warnings);
    }

    /// <summary>Replays captured samples through the settings: before/after proof shown to the user before saving.</summary>
    public static CorrectionCheck Check(
        StickSettings settings, IReadOnlyList<(double X, double Y)> rest, IReadOnlyList<(double X, double Y)> sweep)
    {
        double before = 0, after = 0, right = 0, left = 0, up = 0, down = 0;
        var corrector = new StickCorrector(settings);
        foreach (var (x, y) in rest)
        {
            var (ox, oy) = corrector.Apply(x, y, 0.008);
            before = Math.Max(before, Hypot(x, y));
            after = Math.Max(after, Hypot(ox, oy));
        }
        foreach (var (x, y) in sweep)
        {
            var (ox, oy) = corrector.Apply(x, y, 0.008);
            right = Math.Max(right, ox);
            left = Math.Max(left, -ox);
            up = Math.Max(up, oy);
            down = Math.Max(down, -oy);
        }
        return new CorrectionCheck(before, after, Math.Min(Math.Min(right, left), Math.Min(up, down)));
    }

    public static double ReturnScatter(IReadOnlyList<RestRun> runs)
    {
        var scatter = 0.0;
        for (var i = 0; i < runs.Count; i++)
            for (var j = i + 1; j < runs.Count; j++)
                scatter = Math.Max(scatter, Hypot(runs[i].X.Median - runs[j].X.Median, runs[i].Y.Median - runs[j].Y.Median));
        return scatter;
    }

    private static (AxisCalibration Axis, bool Ok) BuildAxis(double center, AxisRange range)
    {
        // 2% margin so a worn stick that falls just short of its measured peak still reaches full output.
        var positive = (range.Max - center) * 0.98;
        var negative = (center - range.Min) * 0.98;
        var ok = positive >= 0.3 && negative >= 0.3;
        return (ok
            ? new AxisCalibration { Center = center, RangePositive = positive, RangeNegative = negative }
            : new AxisCalibration { Center = center, RangePositive = 1 - center, RangeNegative = 1 + center }, ok);
    }

    private static bool Restricted(AxisRange range, double center) =>
        range.Max - center < MinHalfRange || center - range.Min < MinHalfRange;

    private static double Wander(AxisStats s) => s.SegmentSpread;

    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    private static AxisStats Stats(double[] v, double rateHz)
    {
        var sorted = v.Order().ToArray();
        var mean = v.Average();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var std = Math.Sqrt(v.Sum(a => (a - mean) * (a - mean)) / v.Length);
        var jitter = v.Skip(1).Zip(v, (a, b) => Math.Abs(a - b)).Average();

        // Least-squares slope, converted from per-sample to per-second.
        var midIndex = (v.Length - 1) / 2.0;
        var denominator = Enumerable.Range(0, v.Length).Sum(i => (i - midIndex) * (i - midIndex));
        var slope = Enumerable.Range(0, v.Length).Sum(i => (i - midIndex) * (v[i] - mean)) / denominator;

        // Four-segment means: catches a wander a noisy full-window average would hide.
        var segment = v.Length / 4;
        var segments = Enumerable.Range(0, 4).Select(i => v.Skip(i * segment).Take(segment).Average()).ToArray();

        return new AxisStats(mean, median, sorted[0], sorted[^1], std, jitter, slope * rateHz, segments.Max() - segments.Min());
    }
}
