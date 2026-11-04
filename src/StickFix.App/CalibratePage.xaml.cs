using System.Windows;
using System.Windows.Controls;
using StickFix.Core;
using StickFix.Windows;

namespace StickFix.App;

public partial class CalibratePage : UserControl
{
    private sealed class Capture
    {
        public readonly List<(double X, double Y)> LeftRest = [], RightRest = [], LeftSweep = [], RightSweep = [];
        public readonly List<RestRun> LeftRuns = [], RightRuns = [];
        public AxisRange LeftX = new(0, 0), LeftY = new(0, 0), RightX = new(0, 0), RightY = new(0, 0);
    }

    private static readonly (StickFinding Finding, string Text)[] Plain =
    [
        (StickFinding.ConsistentOffset, "steady off-center drift (software fixes this well)"),
        (StickFinding.Noisy, "jittery at rest"),
        (StickFinding.UnstableDrift, "rest position keeps wandering"),
        (StickFinding.DoesNotReturn, "doesn't return to the same center"),
        (StickFinding.RestrictedRange, "doesn't reach full travel"),
        (StickFinding.PossibleDamage, "possible physical damage"),
        (StickFinding.MovedDuringTest, "moved during measurement"),
    ];

    private const int TotalSteps = 5;
    private PadService _service = null!;
    private Action _finished = () => { };
    private CancellationTokenSource? _cancel;
    private Capture? _capture;
    private Recommendation? _left, _right;
    private bool _resumeAfter;

    public bool IsMeasuring => _cancel is not null;

    public CalibratePage() => InitializeComponent();

    public void Attach(PadService service, Action finished)
    {
        _service = service;
        _finished = finished;
    }

    public void Update(PadView v)
    {
        BeginButton.IsEnabled = v.Connected;
        IntroStatus.Text = v.Connected ? $"Ready: {v.ControllerName}." : "Plug in your controller to begin.";
    }

    private async void OnBegin(object sender, RoutedEventArgs e)
    {
        var slot = FindSlot();
        if (slot is null)
        {
            Show(IntroPanel);
            return;
        }

        Show(RunPanel);
        _cancel = new CancellationTokenSource();
        var capture = new Capture();
        try
        {
            await Rest(slot.Value, capture, 1, _cancel.Token);
            await Sweep(slot.Value, capture, 2, _cancel.Token);
            await Rest(slot.Value, capture, 3, _cancel.Token);
            await Step(slot.Value, 4, "Flick both sticks", "Push both sticks around quickly in every direction. Keep going until this step ends.", 4, _ => { }, _cancel.Token);
            await Rest(slot.Value, capture, 5, _cancel.Token);

            _capture = capture;
            ShowResult();
        }
        catch (OperationCanceledException)
        {
            Show(IntroPanel);
            Resume();
        }
        catch (PadDisconnectedException ex)
        {
            Show(IntroPanel);
            IntroStatus.Text = ex.Message;
            Resume();
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    // The virtual pad must be gone first, so we can't mistake it for the real controller.
    private int? FindSlot()
    {
        _resumeAfter = _service.IsRunning;
        if (_service.IsRunning)
        {
            _service.Stop();
            Thread.Sleep(300); // Windows removes the virtual pad asynchronously.
        }
        return XInput.Scan().FirstOrDefault()?.Slot;
    }

    private void Resume()
    {
        if (_resumeAfter) _service.UserStart();
        _resumeAfter = false;
    }

    private async Task Rest(int slot, Capture capture, int step, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var left = new List<(double X, double Y)>();
            var right = new List<(double X, double Y)>();

            StepTitle.Text = "Let go of the sticks";
            StepText.Text = "Release both sticks and leave the controller on a stable surface. Do not touch the sticks during measurement.";
            StepProgress.Value = 0;
            await Task.Delay(1500, token);

            await Step(slot, step,
                attempt == 1 ? "Measuring, don't touch" : "A stick moved. Measuring again, don't touch",
                "Release both sticks and leave the controller on a stable surface. Do not touch the sticks during measurement.",
                3,
                s =>
                {
                    left.Add((Axis.ToUnit(s.LeftX), Axis.ToUnit(s.LeftY)));
                    right.Add((Axis.ToUnit(s.RightX), Axis.ToUnit(s.RightY)));
                },
                token);

            var leftRun = Diagnosis.Analyze(left, Sampler.RateHz);
            var rightRun = Diagnosis.Analyze(right, Sampler.RateHz);

            // Keep the last attempt even if it moved: a stick that never settles is itself the finding.
            if ((!leftRun.Moved && !rightRun.Moved) || attempt == maxAttempts)
            {
                capture.LeftRuns.Add(leftRun);
                capture.RightRuns.Add(rightRun);
                capture.LeftRest.AddRange(left);
                capture.RightRest.AddRange(right);
                return;
            }
        }
    }

    private async Task Sweep(int slot, Capture capture, int step, CancellationToken token)
    {
        await Step(slot, step, "Rotate both sticks",
            "Roll both sticks slowly around their full outer edge, pressing all the way out, until this step ends.", 8,
            s =>
            {
                capture.LeftSweep.Add((Axis.ToUnit(s.LeftX), Axis.ToUnit(s.LeftY)));
                capture.RightSweep.Add((Axis.ToUnit(s.RightX), Axis.ToUnit(s.RightY)));
            },
            token);

        capture.LeftX = RangeOf(capture.LeftSweep.Select(p => p.X));
        capture.LeftY = RangeOf(capture.LeftSweep.Select(p => p.Y));
        capture.RightX = RangeOf(capture.RightSweep.Select(p => p.X));
        capture.RightY = RangeOf(capture.RightSweep.Select(p => p.Y));
    }

    private static AxisRange RangeOf(IEnumerable<double> values)
    {
        var all = values.ToList();
        return new AxisRange(all.Min(), all.Max());
    }

    private async Task Step(int slot, int step, string title, string text, double seconds, Action<PadState> onSample, CancellationToken token)
    {
        StepTitle.Text = title;
        StepText.Text = text;
        OverallText.Text = $"Step {step} of {TotalSteps}";
        StepProgress.Value = 0;

        var progress = new Progress<double>(p => StepProgress.Value = p);
        var duration = TimeSpan.FromSeconds(seconds);
        var total = duration.TotalSeconds * Sampler.RateHz;
        await Task.Run(() =>
        {
            var taken = 0;
            Sampler.Run(slot, duration, s =>
            {
                onSample(s);
                if (++taken % 12 == 0) ((IProgress<double>)progress).Report(taken / total);
            }, token);
        }, token);
    }

    private CorrectionStrength Strength =>
        Minimal.IsChecked == true ? CorrectionStrength.Minimal
        : Strong.IsChecked == true ? CorrectionStrength.Strong
        : CorrectionStrength.Balanced;

    private void ShowResult()
    {
        var c = _capture!;
        _left = Diagnosis.Recommend(c.LeftRuns, c.LeftX, c.LeftY, Strength);
        _right = Diagnosis.Recommend(c.RightRuns, c.RightX, c.RightY, Strength);

        LeftResult.Text = Describe("Left stick", _left, Diagnosis.Check(_left.Settings, c.LeftRest, c.LeftSweep));
        RightResult.Text = Describe("Right stick", _right, Diagnosis.Check(_right.Settings, c.RightRest, c.RightSweep));

        var warnings = _left.Warnings.Select(w => $"Left: {w}").Concat(_right.Warnings.Select(w => $"Right: {w}")).ToList();
        Warnings.Text = string.Join("\n", warnings);
        Warnings.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var ok = _left.AutoApplyAllowed && _right.AutoApplyAllowed;
        var moved = (_left.Findings | _right.Findings).HasFlag(StickFinding.MovedDuringTest);
        ResultTitle.Text = ok ? "Here's what StickFix found" : "This needs your attention";
        OverrideCheck.Visibility = !ok && !moved ? Visibility.Visible : Visibility.Collapsed;
        OverrideCheck.IsChecked = false;
        SaveButton.IsEnabled = ok;
        Show(ResultPanel);
    }

    private static string Describe(string name, Recommendation rec, CorrectionCheck check)
    {
        var verdict = rec.Findings == StickFinding.None ? "healthy"
            : string.Join(", ", Plain.Where(p => rec.Findings.HasFlag(p.Finding)).Select(p => p.Text));
        return $"{name}: {verdict}.\n"
             + $"   At rest, off center: {check.RestBefore:P1} before, {check.RestAfter:P1} after.\n"
             + $"   Full push still reaches {check.MinReach:P0} in its weakest direction. Deadzone {rec.Settings.DeadzoneInner:P1}. Confidence: {rec.Confidence}.";
    }

    private void OnStrength(object sender, RoutedEventArgs e)
    {
        if (_capture is not null) ShowResult();
    }

    private void OnOverride(object sender, RoutedEventArgs e) => SaveButton.IsEnabled = OverrideCheck.IsChecked == true;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _service.SetProfile(new Profile
        {
            ControllerId = _service.ControllerId,
            Left = _left!.Settings,
            Right = _right!.Settings,
            Confidence = (Confidence)Math.Min((int)_left.Confidence, (int)_right.Confidence),
            LeftFindings = _left.Findings,
            RightFindings = _right.Findings,
        });
        Log.Write($"Calibration saved: left {_left.Findings}, right {_right.Findings}.");
        _capture = null;
        Show(IntroPanel);
        _service.UserStart();
        _resumeAfter = false;
        _finished();
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        _capture = null;
        Show(IntroPanel);
        Resume();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private void Show(UIElement panel)
    {
        foreach (var p in new UIElement[] { IntroPanel, RunPanel, ResultPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }
}
