namespace StickFix.Core;

/// <summary>Formulas are documented in docs/ARCHITECTURE.md section 5.</summary>
public sealed record AxisCalibration
{
    public double Center { get; init; }
    public double RangePositive { get; init; } = 1.0;
    public double RangeNegative { get; init; } = 1.0;
    public bool Invert { get; init; }

    public double Normalize(double x)
    {
        if (!double.IsFinite(x)) return 0;
        var delta = x - Center;
        var range = delta >= 0 ? RangePositive : RangeNegative;
        return range > 1e-6 ? Math.Clamp(delta / range, -1.0, 1.0) : 0;
    }
}

public sealed record StickSettings
{
    public AxisCalibration X { get; init; } = new();
    public AxisCalibration Y { get; init; } = new();
    public bool SwapXY { get; init; }
    public double DeadzoneInner { get; init; }
    public double DeadzoneOuter { get; init; } = 1.0;
    public double Hysteresis { get; init; }
    public double Gamma { get; init; } = 1.0;
    public double Sensitivity { get; init; } = 1.0;
    public bool FilterEnabled { get; init; }
    public double FilterMinCutoffHz { get; init; } = 5.0;
    public double FilterBeta { get; init; } = 1.0;

    /// <summary>Returns a problem description, or null when the settings are usable.</summary>
    public string? Validate()
    {
        double[] all =
        [
            X.Center, X.RangePositive, X.RangeNegative, Y.Center, Y.RangePositive, Y.RangeNegative,
            DeadzoneInner, DeadzoneOuter, Hysteresis, Gamma, Sensitivity, FilterMinCutoffHz, FilterBeta,
        ];
        if (all.Any(v => !double.IsFinite(v))) return "contains a non-finite number";
        if (Math.Abs(X.Center) >= 1 || Math.Abs(Y.Center) >= 1) return "center must be inside (-1, 1)";
        if (Math.Min(Math.Min(X.RangePositive, X.RangeNegative), Math.Min(Y.RangePositive, Y.RangeNegative)) <= 0)
            return "ranges must be positive";
        if (DeadzoneInner < 0 || DeadzoneOuter > 1 || DeadzoneInner >= DeadzoneOuter)
            return "deadzones must satisfy 0 <= inner < outer <= 1";
        if (Hysteresis < 0) return "hysteresis must not be negative";
        if (Gamma <= 0 || Sensitivity <= 0) return "gamma and sensitivity must be positive";
        return FilterMinCutoffHz > 0 && FilterBeta >= 0 ? null : "filter settings out of range";
    }
}

/// <summary>Stateful per-stick pipeline: swap, calibrate, optional filter, radial shaping, invert.</summary>
public sealed class StickCorrector(StickSettings settings)
{
    private readonly OneEuroFilter _fx = new(settings.FilterMinCutoffHz, settings.FilterBeta);
    private readonly OneEuroFilter _fy = new(settings.FilterMinCutoffHz, settings.FilterBeta);
    private bool _resting = true;

    public (double X, double Y) Apply(double rawX, double rawY, double dtSeconds)
    {
        if (settings.SwapXY) (rawX, rawY) = (rawY, rawX);
        var x = settings.X.Normalize(rawX);
        var y = settings.Y.Normalize(rawY);

        if (settings.FilterEnabled)
        {
            x = _fx.Filter(x, dtSeconds);
            y = _fy.Filter(y, dtSeconds);
        }

        var m = Math.Sqrt(x * x + y * y);
        var threshold = settings.DeadzoneInner + (_resting ? settings.Hysteresis : 0);
        _resting = m <= threshold;
        if (_resting || m < 1e-9) return (0, 0);

        var scaled = Math.Min(1.0, (m - settings.DeadzoneInner) / (settings.DeadzoneOuter - settings.DeadzoneInner));
        var shaped = Math.Min(1.0, settings.Sensitivity * Math.Pow(scaled, settings.Gamma));
        var outX = x / m * shaped;
        var outY = y / m * shaped;
        return (settings.X.Invert ? -outX : outX, settings.Y.Invert ? -outY : outY);
    }

    public void Reset()
    {
        _fx.Reset();
        _fy.Reset();
        _resting = true;
    }
}

public sealed class PadCorrector(StickSettings left, StickSettings right)
{
    private readonly StickCorrector _left = new(left);
    private readonly StickCorrector _right = new(right);

    public PadState Apply(PadState raw, double dtSeconds)
    {
        var (lx, ly) = _left.Apply(Axis.ToUnit(raw.LeftX), Axis.ToUnit(raw.LeftY), dtSeconds);
        var (rx, ry) = _right.Apply(Axis.ToUnit(raw.RightX), Axis.ToUnit(raw.RightY), dtSeconds);
        return raw with
        {
            LeftX = Axis.ToShort(lx),
            LeftY = Axis.ToShort(ly),
            RightX = Axis.ToShort(rx),
            RightY = Axis.ToShort(ry),
        };
    }

    public void Reset()
    {
        _left.Reset();
        _right.Reset();
    }
}

/// <summary>One Euro filter (Casiez et al. 2012): smooths slow motion, adds little lag to fast motion.</summary>
public sealed class OneEuroFilter(double minCutoffHz, double beta, double derivativeCutoffHz = 1.0)
{
    private bool _primed;
    private double _x, _dx;

    public double Filter(double value, double dtSeconds)
    {
        if (!_primed || dtSeconds <= 0)
        {
            _primed = true;
            _x = value;
            _dx = 0;
            return value;
        }

        _dx += Alpha(derivativeCutoffHz, dtSeconds) * ((value - _x) / dtSeconds - _dx);
        var cutoff = minCutoffHz + beta * Math.Abs(_dx);
        _x += Alpha(cutoff, dtSeconds) * (value - _x);
        return _x;
    }

    public void Reset() => _primed = false;

    private static double Alpha(double cutoffHz, double dt) => 1.0 / (1.0 + 1.0 / (2 * Math.PI * cutoffHz * dt));
}
