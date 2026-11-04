using StickFix.Core;
using Xunit;

namespace StickFix.Tests;

public class AxisTests
{
    [Fact]
    public void Every_short_value_round_trips_exactly()
    {
        for (int v = short.MinValue; v <= short.MaxValue; v++)
            Assert.Equal((short)v, Axis.ToShort(Axis.ToUnit((short)v)));
    }

    [Theory]
    [InlineData(2.0, short.MaxValue)]
    [InlineData(-2.0, short.MinValue)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, short.MaxValue)]
    public void ToShort_handles_out_of_range_and_invalid(double input, short expected) =>
        Assert.Equal(expected, Axis.ToShort(input));
}

public class CalibrationTests
{
    [Fact]
    public void Center_maps_to_zero() =>
        Assert.Equal(0, new AxisCalibration { Center = -0.21 }.Normalize(-0.21), 9);

    [Fact]
    public void Each_side_uses_its_own_range()
    {
        var axis = new AxisCalibration { Center = -0.21, RangePositive = 1.21, RangeNegative = 0.79 };
        Assert.Equal(1.0, axis.Normalize(1.0), 9);
        Assert.Equal(-1.0, axis.Normalize(-1.0), 9);
        Assert.Equal(0.5, axis.Normalize(-0.21 + 0.605), 9);
    }

    [Fact]
    public void Values_beyond_range_saturate()
    {
        var axis = new AxisCalibration { RangePositive = 0.8, RangeNegative = 0.8 };
        Assert.Equal(1.0, axis.Normalize(0.95));
        Assert.Equal(-1.0, axis.Normalize(-0.95));
    }

    [Fact]
    public void Zero_range_and_invalid_input_do_not_divide_by_zero()
    {
        var axis = new AxisCalibration { RangePositive = 0, RangeNegative = 0 };
        Assert.Equal(0, axis.Normalize(0.5));
        Assert.Equal(0, new AxisCalibration().Normalize(double.NaN));
        Assert.Equal(0, new AxisCalibration().Normalize(double.PositiveInfinity));
    }
}

public class StickCorrectorTests
{
    private static (double X, double Y) Once(StickSettings s, double x, double y) => new StickCorrector(s).Apply(x, y, 0.004);

    [Fact]
    public void Default_settings_pass_values_through()
    {
        var (x, y) = Once(new StickSettings(), 0.3, -0.4);
        Assert.Equal(0.3, x, 9);
        Assert.Equal(-0.4, y, 9);
    }

    [Fact]
    public void Inside_deadzone_is_exactly_zero()
    {
        Assert.Equal((0.0, 0.0), Once(new StickSettings { DeadzoneInner = 0.1 }, 0.05, 0.05));
    }

    [Fact]
    public void Deadzone_rescales_so_output_is_continuous_and_reaches_full()
    {
        var s = new StickSettings { DeadzoneInner = 0.1 };
        Assert.Equal(0.001 / 0.9, Once(s, 0.101, 0).X, 6);
        Assert.Equal(1.0, Once(s, 1.0, 0).X, 9);
    }

    [Fact]
    public void Small_intentional_movement_survives_just_outside_deadzone()
    {
        Assert.True(Once(new StickSettings { DeadzoneInner = 0.05 }, 0.08, 0).X > 0.03);
    }

    [Fact]
    public void Radial_deadzone_preserves_direction()
    {
        var (x, y) = Once(new StickSettings { DeadzoneInner = 0.2 }, 0.3, 0.4);
        Assert.Equal(0.75, x / y, 9);
    }

    [Fact]
    public void Outer_deadzone_saturates_early()
    {
        Assert.Equal(1.0, Once(new StickSettings { DeadzoneOuter = 0.9 }, 0.95, 0).X, 9);
    }

    [Fact]
    public void Inversion_flips_only_the_chosen_axis()
    {
        var (x, y) = Once(new StickSettings { Y = new AxisCalibration { Invert = true } }, 0.3, 0.4);
        Assert.Equal(0.3, x, 9);
        Assert.Equal(-0.4, y, 9);
    }

    [Fact]
    public void Swap_exchanges_axes()
    {
        var (x, y) = Once(new StickSettings { SwapXY = true }, 0.3, 0.4);
        Assert.Equal(0.4, x, 9);
        Assert.Equal(0.3, y, 9);
    }

    [Fact]
    public void Gamma_curve_bends_the_middle_and_keeps_endpoints()
    {
        var s = new StickSettings { Gamma = 2 };
        Assert.Equal(0.25, Once(s, 0.5, 0).X, 9);
        Assert.Equal(1.0, Once(s, 1.0, 0).X, 9);
    }

    [Fact]
    public void Sensitivity_scales_and_clamps()
    {
        var s = new StickSettings { Sensitivity = 2 };
        Assert.Equal(0.6, Once(s, 0.3, 0).X, 9);
        Assert.Equal(1.0, Once(s, 0.8, 0).X, 9);
    }

    [Fact]
    public void Corner_input_never_exceeds_unit_circle()
    {
        var (x, y) = Once(new StickSettings(), 1.0, 1.0);
        Assert.True(x * x + y * y <= 1.0 + 1e-9);
    }

    [Fact]
    public void Hysteresis_stops_chatter_at_the_deadzone_edge()
    {
        var c = new StickCorrector(new StickSettings { DeadzoneInner = 0.10, Hysteresis = 0.02 });
        Assert.Equal(0, c.Apply(0.11, 0, 0.004).X);    // In the band, starting at rest: stays at rest.
        Assert.True(c.Apply(0.13, 0, 0.004).X > 0);    // Past the band: wakes.
        Assert.True(c.Apply(0.11, 0, 0.004).X > 0);    // In the band, already moving: keeps moving.
        Assert.Equal(0, c.Apply(0.09, 0, 0.004).X);    // Below inner: rests again.
    }

    [Fact]
    public void Measured_prototype_offset_is_removed()
    {
        var s = new StickSettings
        {
            Y = new AxisCalibration { Center = -0.20958, RangePositive = 1.20958, RangeNegative = 0.79042 },
            DeadzoneInner = 0.03,
        };
        Assert.Equal((0.0, 0.0), Once(s, 0, -0.205));
        Assert.Equal(1.0, Once(s, 0, 1.0).Y, 9);
        Assert.Equal(-1.0, Once(s, 0, -1.0).Y, 9);
    }

    [Theory]
    [InlineData(0.2, 0.1)]
    [InlineData(-0.1, 1.5)]
    public void Validate_rejects_bad_deadzones(double inner, double outer) =>
        Assert.NotNull(new StickSettings { DeadzoneInner = inner, DeadzoneOuter = outer }.Validate());

    [Fact]
    public void Validate_rejects_nan_and_accepts_defaults()
    {
        Assert.NotNull(new StickSettings { Gamma = double.NaN }.Validate());
        Assert.Null(new StickSettings().Validate());
    }
}

public class OneEuroFilterTests
{
    [Fact]
    public void Reduces_noise_at_rest()
    {
        var filter = new OneEuroFilter(5, 1);
        var random = new Random(1);
        double inSum = 0, outSum = 0;
        for (var i = 0; i < 1000; i++)
        {
            var noise = (random.NextDouble() - 0.5) * 0.02;
            var filtered = filter.Filter(noise, 0.004);
            if (i <= 100) continue;
            inSum += noise * noise;
            outSum += filtered * filtered;
        }
        Assert.True(outSum < inSum * 0.5, $"filtered power {outSum} vs raw {inSum}");
    }

    [Fact]
    public void Follows_a_fast_flick_within_20ms()
    {
        var filter = new OneEuroFilter(5, 1);
        for (var i = 0; i < 50; i++) filter.Filter(0, 0.004);
        var value = 0.0;
        for (var i = 0; i < 5; i++) value = filter.Filter(1, 0.004);
        Assert.True(value > 0.8, $"reached only {value} after 20 ms");
    }

    [Fact]
    public void Zero_dt_does_not_produce_nan()
    {
        var filter = new OneEuroFilter(5, 1);
        filter.Filter(0.5, 0.004);
        Assert.True(double.IsFinite(filter.Filter(0.6, 0)));
    }
}
