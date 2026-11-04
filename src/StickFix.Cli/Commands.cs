using System.Diagnostics;
using StickFix.Core;
using StickFix.Windows;

namespace StickFix.Cli;

public static class Commands
{
    private const double SampleHz = 125;

    public static int Devices()
    {
        var pads = XInput.Scan();
        Console.WriteLine(pads.Count == 0 ? "No XInput controllers connected." : $"{pads.Count} XInput controller(s):");
        foreach (var pad in pads) Console.WriteLine($"  slot {pad.Slot}  id {pad.ControllerId}");
        return 0;
    }

    public static int SelfTest()
    {
        var before = XInput.Scan().Select(p => p.Slot).ToHashSet();
        Console.WriteLine($"Before: pads on slots [{string.Join(", ", before)}]");

        using var timer = new HighResTimer();
        using var pad = ViGEmPad.Create();
        Console.WriteLine($"Virtual pad created, Windows assigned slot {pad.Slot}.");
        if (before.Contains(pad.Slot))
        {
            Console.WriteLine("FAIL: that slot was already in use before we created the pad.");
            return 1;
        }

        var result = LoopbackVerifier.Run(
            pad,
            () => XInput.TryRead(pad.Slot, out var s) ? s : null,
            TimeSpan.FromMilliseconds(250),
            () => Thread.Sleep(1));

        foreach (var failure in result.Failures) Console.WriteLine($"  {failure}");
        Console.WriteLine(result.Passed
            ? $"PASS: all {result.Steps} states read back identical through XInput."
            : $"FAIL: {result.Failures.Count} of {result.Steps} states did not match.");
        return result.Passed ? 0 : 1;
    }

    public static int Raw(Options options)
    {
        var slot = options.PickPhysical().Slot;
        Console.WriteLine($"RAW physical values, slot {slot}. Nothing is being forwarded. Ctrl+C to stop.");
        using var cancel = CancelOnCtrlC();
        while (!cancel.IsCancellationRequested)
        {
            WriteStatus(XInput.TryRead(slot, out var s) ? $"RAW {Format(s)}" : "disconnected");
            Thread.Sleep(50);
        }
        Console.WriteLine();
        return 0;
    }

    public static int Rest(Options options)
    {
        var info = options.PickPhysical();
        var (left, right) = MeasureRest(info.Slot);
        Report("Left", left, Diagnosis.Classify([left]));
        Report("Right", right, Diagnosis.Classify([right]));
        return 0;
    }

    public static int Calibrate(Options options)
    {
        var info = options.PickPhysical();
        var strength = options.Strength;

        var (left1, right1) = MeasureRest(info.Slot);
        var (rangeLX, rangeLY, rangeRX, rangeRY) = MeasureRange(info.Slot);
        Console.WriteLine("Second measurement, to check the sticks return to the same place.");
        var (left2, right2) = MeasureRest(info.Slot);

        var leftRec = Diagnosis.Recommend([left1, left2], rangeLX, rangeLY, strength);
        var rightRec = Diagnosis.Recommend([right1, right2], rangeRX, rangeRY, strength);
        Report("Left", left2, leftRec);
        Report("Right", right2, rightRec);

        if (!leftRec.AutoApplyAllowed || !rightRec.AutoApplyAllowed)
        {
            Console.WriteLine("Not saving a profile: see the warnings above.");
            return 1;
        }

        Console.Write("Save this profile? [y/N] ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Nothing saved.");
            return 0;
        }

        var profile = new Profile
        {
            ControllerId = info.ControllerId,
            Left = leftRec.Settings,
            Right = rightRec.Settings,
            Confidence = (Confidence)Math.Min((int)leftRec.Confidence, (int)rightRec.Confidence),
            LeftFindings = leftRec.Findings,
            RightFindings = rightRec.Findings,
        };
        var path = ProfileStore.PathFor(ProfileStore.DefaultDirectory, info.ControllerId, profile.Name);
        ProfileStore.Save(profile, path);
        Console.WriteLine($"Saved {path}");
        return 0;
    }

    public static int Run(Options options)
    {
        var info = options.PickPhysical();
        var corrector = LoadCorrector(options, info);
        var excluded = XInput.Scan().Where(p => p.Slot != info.Slot).Select(p => p.Slot).ToHashSet();

        using var timer = new HighResTimer();
        using var pad = ViGEmPad.Create();
        excluded.Add(pad.Slot);
        var physical = new XInputPad(info.Slot, options.Slot is null, excluded);
        pad.Rumble += (large, small) => XInput.SetRumble(physical.Slot, large, small);

        var engine = new ForwardingEngine(physical, pad, corrector);
        Console.WriteLine($"Physical slot {info.Slot} -> virtual slot {pad.Slot}. Correction: {(corrector is null ? "OFF (raw)" : "ON")}.");
        Console.WriteLine("Space toggles bypass on a corrected run. Ctrl+C stops. Games still see the physical pad until hiding exists (Phase 4).");

        using var cancel = CancelOnCtrlC();
        Exception? fault = null;
        var worker = new Thread(() =>
        {
            try { engine.Run(cancel.Token, () => Thread.Sleep(1)); }
            catch (Exception e) { fault = e; cancel.Cancel(); }
        }) { Priority = ThreadPriority.AboveNormal };
        worker.Start();

        var mismatches = 0;
        while (!cancel.IsCancellationRequested)
        {
            var (connected, raw, emitted) = engine.Snapshot();
            var seen = XInput.TryRead(pad.Slot, out var readBack) ? readBack : (PadState?)null;
            Thread.Sleep(5);
            // Only count it when the output held still across the read, otherwise it's just a race.
            if (seen != emitted && engine.Snapshot().Emitted == emitted) mismatches++;

            if (Console.IsInputRedirected is false && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Spacebar)
                engine.Bypass = !engine.Bypass;

            WriteStatus($"{(connected ? "ok" : "DISCONNECTED")}{(engine.Bypass ? " BYPASS" : "")} RAW {Format(raw)} | EMIT {Format(emitted)} | VIRT {(seen is { } v ? Format(v) : "none")} | mismatch {mismatches}");
            Thread.Sleep(45);
        }

        worker.Join();
        Console.WriteLine();
        XInput.SetRumble(physical.Slot, 0, 0);
        if (fault is not null)
        {
            Console.WriteLine($"Stopped on error: {fault.Message}");
            return 1;
        }
        Console.WriteLine($"Stopped. Virtual pad released. Read-back mismatches: {mismatches}.");
        return 0;
    }

    private static PadCorrector? LoadCorrector(Options options, SlotInfo info)
    {
        if (options.Has("--raw")) return null;
        var path = options.Value("--profile") ?? ProfileStore.PathFor(ProfileStore.DefaultDirectory, info.ControllerId, "Default");
        if (!File.Exists(path))
            throw new UsageException($"No profile at {path}. Run 'stickfix calibrate' first, or use --raw.");
        var profile = ProfileStore.Load(path);
        return new PadCorrector(profile.Left, profile.Right);
    }

    private static (RestRun Left, RestRun Right) MeasureRest(int slot)
    {
        Console.WriteLine("Release both sticks and leave the controller on a stable surface. Do not touch the sticks during measurement.");
        Thread.Sleep(2000);
        Console.WriteLine("Measuring for 5 seconds...");
        var left = new List<(double, double)>();
        var right = new List<(double, double)>();
        Sample(slot, TimeSpan.FromSeconds(5), s =>
        {
            left.Add((Axis.ToUnit(s.LeftX), Axis.ToUnit(s.LeftY)));
            right.Add((Axis.ToUnit(s.RightX), Axis.ToUnit(s.RightY)));
        });
        return (Diagnosis.Analyze(left, SampleHz), Diagnosis.Analyze(right, SampleHz));
    }

    private static (AxisRange LX, AxisRange LY, AxisRange RX, AxisRange RY) MeasureRange(int slot)
    {
        Console.WriteLine("Now rotate BOTH sticks around their full outer edge, slowly, for 8 seconds. Starting in 2 seconds...");
        Thread.Sleep(2000);
        double lxMin = 0, lxMax = 0, lyMin = 0, lyMax = 0, rxMin = 0, rxMax = 0, ryMin = 0, ryMax = 0;
        Sample(slot, TimeSpan.FromSeconds(8), s =>
        {
            Track(Axis.ToUnit(s.LeftX), ref lxMin, ref lxMax);
            Track(Axis.ToUnit(s.LeftY), ref lyMin, ref lyMax);
            Track(Axis.ToUnit(s.RightX), ref rxMin, ref rxMax);
            Track(Axis.ToUnit(s.RightY), ref ryMin, ref ryMax);
        });
        Console.WriteLine("Done. Release the sticks.");
        return (new(lxMin, lxMax), new(lyMin, lyMax), new(rxMin, rxMax), new(ryMin, ryMax));
    }

    private static void Track(double v, ref double min, ref double max)
    {
        min = Math.Min(min, v);
        max = Math.Max(max, v);
    }

    private static void Sample(int slot, TimeSpan duration, Action<PadState> onSample)
    {
        using var timer = new HighResTimer();
        var clock = Stopwatch.StartNew();
        long taken = 0;
        while (clock.Elapsed < duration)
        {
            if (!XInput.TryRead(slot, out var state))
                throw new UsageException("The controller disconnected during measurement. Reconnect it and try again.");
            onSample(state);
            taken++;
            var due = TimeSpan.FromSeconds(taken / SampleHz) - clock.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
        }
    }

    private static void Report(string stick, RestRun run, StickFinding findings)
    {
        Console.WriteLine($"{stick} stick");
        Console.WriteLine($"  X  mean {run.X.Mean:+0.0000;-0.0000}  median {run.X.Median:+0.0000;-0.0000}  min {run.X.Min:+0.0000;-0.0000}  max {run.X.Max:+0.0000;-0.0000}  std {run.X.StdDev:0.0000}  jitter {run.X.Jitter:0.0000}  drift/s {run.X.DriftPerSecond:+0.0000;-0.0000}");
        Console.WriteLine($"  Y  mean {run.Y.Mean:+0.0000;-0.0000}  median {run.Y.Median:+0.0000;-0.0000}  min {run.Y.Min:+0.0000;-0.0000}  max {run.Y.Max:+0.0000;-0.0000}  std {run.Y.StdDev:0.0000}  jitter {run.Y.Jitter:0.0000}  drift/s {run.Y.DriftPerSecond:+0.0000;-0.0000}");
        Console.WriteLine($"  Offset from zero {run.Offset:0.0000}, 99% of samples within {run.R99:0.0000} of center");
        Console.WriteLine($"  Findings: {(findings == StickFinding.None ? "healthy" : findings.ToString())}");
    }

    private static void Report(string stick, RestRun run, Recommendation rec)
    {
        Report(stick, run, rec.Findings);
        var s = rec.Settings;
        Console.WriteLine($"  Recommended: center ({s.X.Center:+0.0000;-0.0000}, {s.Y.Center:+0.0000;-0.0000}), inner deadzone {s.DeadzoneInner:0.000}, confidence {rec.Confidence}");
        foreach (var warning in rec.Warnings) Console.WriteLine($"  WARNING: {warning}");
    }

    private static string Format(PadState s) =>
        $"L({Axis.ToUnit(s.LeftX):+0.000;-0.000},{Axis.ToUnit(s.LeftY):+0.000;-0.000}) R({Axis.ToUnit(s.RightX):+0.000;-0.000},{Axis.ToUnit(s.RightY):+0.000;-0.000}) T({s.LeftTrigger,3},{s.RightTrigger,3}) B{s.Buttons:X4}";

    private static void WriteStatus(string text)
    {
        var width = Console.IsOutputRedirected ? text.Length : Math.Max(1, Console.WindowWidth - 1);
        Console.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
    }

    private static CancellationTokenSource CancelOnCtrlC()
    {
        var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        return cancel;
    }
}
