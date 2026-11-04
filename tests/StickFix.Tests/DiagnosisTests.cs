using StickFix.Core;
using Xunit;

namespace StickFix.Tests;

public class DiagnosisTests
{
    private const double Rate = 125;
    private static readonly AxisRange Full = new(-1, 1);

    private static RestRun Rest(double cx, double cy, double noise = 0.001, double driftPerSecond = 0, int count = 625, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new List<(double, double)>();
        for (var i = 0; i < count; i++)
        {
            var drift = driftPerSecond * i / Rate;
            samples.Add((cx + (random.NextDouble() - 0.5) * noise * 3.46 + drift, cy + (random.NextDouble() - 0.5) * noise * 3.46));
        }
        return Diagnosis.Analyze(samples, Rate);
    }

    [Fact]
    public void Stats_are_correct_on_known_data()
    {
        var samples = Enumerable.Range(0, 100).Select(i => (i % 2 == 0 ? 0.1 : 0.3, 0.0)).ToList();
        var x = Diagnosis.Analyze(samples, Rate).X;
        Assert.Equal(0.2, x.Mean, 9);
        Assert.Equal(0.2, x.Median, 9);
        Assert.Equal(0.1, x.Min);
        Assert.Equal(0.3, x.Max);
        Assert.Equal(0.1, x.StdDev, 9);
        Assert.Equal(0.2, x.Jitter, 9);
    }

    [Fact]
    public void Slope_is_reported_per_second()
    {
        var samples = Enumerable.Range(0, 625).Select(i => (0.01 * i / Rate, 0.0)).ToList();
        Assert.Equal(0.01, Diagnosis.Analyze(samples, Rate).X.DriftPerSecond, 6);
    }

    [Fact]
    public void Too_few_samples_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Diagnosis.Analyze([(0, 0)], Rate));

    [Fact]
    public void Healthy_stick_has_no_findings() =>
        Assert.Equal(StickFinding.None, Diagnosis.Classify([Rest(0.005, -0.005)]));

    [Fact]
    public void Stable_offset_is_consistent_offset_only() =>
        Assert.Equal(StickFinding.ConsistentOffset, Diagnosis.Classify([Rest(-0.02, -0.2096)]));

    [Fact]
    public void Random_noise_around_center_is_noisy_not_offset() =>
        Assert.Equal(StickFinding.Noisy, Diagnosis.Classify([Rest(0, 0, noise: 0.015)]));

    [Fact]
    public void Slow_wander_is_unstable() =>
        Assert.True(Diagnosis.Classify([Rest(0, 0, driftPerSecond: 0.01)]).HasFlag(StickFinding.UnstableDrift));

    [Fact]
    public void One_moved_run_asks_for_a_retry_only()
    {
        var run = Rest(0, 0, driftPerSecond: 0.1);
        Assert.True(run.Moved);
        Assert.Equal(StickFinding.MovedDuringTest, Diagnosis.Classify([run]));
    }

    [Fact]
    public void Two_moved_runs_in_a_row_are_unstable_and_possibly_damaged()
    {
        var run = Rest(0, 0, driftPerSecond: 0.1);
        Assert.Equal(StickFinding.UnstableDrift | StickFinding.PossibleDamage, Diagnosis.Classify([run, run]));
    }

    [Fact]
    public void Different_center_after_moving_is_does_not_return() =>
        Assert.True(Diagnosis.Classify([Rest(0, 0), Rest(0.1, 0)]).HasFlag(StickFinding.DoesNotReturn));

    [Fact]
    public void Huge_offset_flags_possible_damage() =>
        Assert.True(Diagnosis.Classify([Rest(0, 0.5)]).HasFlag(StickFinding.PossibleDamage));

    [Fact]
    public void Short_travel_is_restricted_range() =>
        Assert.True(Diagnosis.Classify([Rest(0, 0)], new AxisRange(-0.5, 1), Full).HasFlag(StickFinding.RestrictedRange));

    [Theory]
    [InlineData(0.05, 0.9, true)]
    [InlineData(0.9, 0.05, false)]
    [InlineData(0.05, -0.9, false)]
    public void Axis_mapping_check(double peakX, double peakY, bool expected) =>
        Assert.Equal(expected, Diagnosis.AxisMappingLooksCorrect(peakX, peakY));

    [Fact]
    public void Recommendation_centers_the_stick_and_stays_small_for_stable_offset()
    {
        var runs = new[] { Rest(-0.02, -0.21, seed: 1), Rest(-0.02, -0.21, seed: 2) };
        var rec = Diagnosis.Recommend(runs, Full, Full, CorrectionStrength.Balanced);

        Assert.True(rec.AutoApplyAllowed);
        Assert.Equal(Confidence.High, rec.Confidence);
        Assert.Equal(-0.21, rec.Settings.Y.Center, 2);
        Assert.Equal(0.98 * 1.21, rec.Settings.Y.RangePositive, 2);
        Assert.Equal(0.98 * 0.79, rec.Settings.Y.RangeNegative, 2);
        Assert.True(rec.Settings.DeadzoneInner < 0.05, $"deadzone was {rec.Settings.DeadzoneInner}");
        Assert.Null(rec.Settings.Validate());
    }

    [Fact]
    public void Recommended_settings_zero_the_rest_noise_and_reach_full_output()
    {
        var runs = new[] { Rest(0.02, -0.21, noise: 0.002, seed: 1), Rest(0.02, -0.21, noise: 0.002, seed: 2) };
        var rec = Diagnosis.Recommend(runs, Full, Full, CorrectionStrength.Balanced);
        var corrector = new StickCorrector(rec.Settings);

        var random = new Random(3);
        for (var i = 0; i < 2000; i++)
        {
            var (x, y) = corrector.Apply(0.02 + (random.NextDouble() - 0.5) * 0.005, -0.21 + (random.NextDouble() - 0.5) * 0.005, 0.004);
            Assert.Equal((0.0, 0.0), (x, y));
        }
        Assert.Equal(1.0, corrector.Apply(0.02, 1.0, 0.004).Y, 6);
        Assert.Equal(-1.0, corrector.Apply(0.02, -1.0, 0.004).Y, 6);
    }

    [Fact]
    public void Strength_presets_scale_the_deadzone()
    {
        var runs = new[] { Rest(0, 0, noise: 0.01, seed: 1), Rest(0, 0, noise: 0.01, seed: 2) };
        double Deadzone(CorrectionStrength s) => Diagnosis.Recommend(runs, Full, Full, s).Settings.DeadzoneInner;
        Assert.True(Deadzone(CorrectionStrength.Minimal) < Deadzone(CorrectionStrength.Balanced));
        Assert.True(Deadzone(CorrectionStrength.Balanced) < Deadzone(CorrectionStrength.Strong));
    }

    [Fact]
    public void Unstable_stick_is_never_auto_applied_and_explains_why()
    {
        var rec = Diagnosis.Recommend([Rest(0, 0), Rest(0.1, 0)], Full, Full, CorrectionStrength.Strong);
        Assert.False(rec.AutoApplyAllowed);
        Assert.Equal(Confidence.Low, rec.Confidence);
        Assert.NotEmpty(rec.Warnings);
    }

    [Fact]
    public void Deadzone_beyond_the_limit_is_refused()
    {
        // Heavy noise plus a return error just under ReturnLimit: wanted is about 2.5 * (0.09 + 0.04) = 0.32.
        var runs = new[] { Rest(0, 0, noise: 0.04, seed: 1), Rest(0.04, 0, noise: 0.04, seed: 2) };
        var rec = Diagnosis.Recommend(runs, Full, Full, CorrectionStrength.Strong);
        Assert.False(rec.AutoApplyAllowed);
        Assert.Contains(rec.Warnings, w => w.Contains("limit"));
        Assert.Equal(Diagnosis.MaxDeadzone, rec.Settings.DeadzoneInner);
    }

    [Fact]
    public void Large_but_allowed_deadzone_still_warns_about_precision()
    {
        var rec = Diagnosis.Recommend([Rest(0, 0, noise: 0.04)], Full, Full, CorrectionStrength.Balanced);
        Assert.True(rec.AutoApplyAllowed);
        Assert.Contains(rec.Warnings, w => w.Contains("precision"));
    }

    [Fact]
    public void Failed_sweep_falls_back_to_valid_ranges_and_blocks_apply()
    {
        var rec = Diagnosis.Recommend([Rest(0, 0)], new AxisRange(0, 0), Full, CorrectionStrength.Balanced);
        Assert.False(rec.AutoApplyAllowed);
        Assert.Null(rec.Settings.Validate());
    }
}
