using System.Diagnostics;

namespace StickFix.Core;

public interface IPhysicalPad
{
    int Slot { get; }
    bool TryRead(out PadState state);
}

public interface IVirtualPad : IDisposable
{
    int Slot { get; }
    void Submit(PadState state);
}

/// <summary>Reads physical, corrects, emits. Guarantees neutral output on disconnect and on any exit.</summary>
public sealed class ForwardingEngine
{
    private readonly IPhysicalPad _physical;
    private readonly IVirtualPad _virtual;
    private readonly PadCorrector? _corrector;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PadState _lastRaw, _lastEmitted;
    private bool _connected, _bypass;
    private double _lastTick;

    public ForwardingEngine(IPhysicalPad physical, IVirtualPad virtualPad, PadCorrector? corrector)
    {
        _physical = physical;
        _virtual = virtualPad;
        _corrector = corrector;
        EnsureNoLoop();
    }

    public bool Bypass
    {
        get { lock (_gate) return _bypass; }
        set { lock (_gate) _bypass = value; }
    }

    public (bool Connected, PadState Raw, PadState Emitted) Snapshot()
    {
        lock (_gate) return (_connected, _lastRaw, _lastEmitted);
    }

    public void Tick()
    {
        EnsureNoLoop();
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTick;
        _lastTick = now;

        var connected = _physical.TryRead(out var raw);
        lock (_gate)
        {
            if (!connected)
            {
                if (_connected) _corrector?.Reset();
                _connected = false;
                _lastRaw = PadState.Neutral;
                Emit(PadState.Neutral);
                return;
            }

            _connected = true;
            _lastRaw = raw;
            Emit(_corrector is null || _bypass ? raw : _corrector.Apply(raw, dt));
        }
    }

    /// <summary>Runs until cancelled. Always leaves the virtual pad neutral, even on a fault.</summary>
    public void Run(CancellationToken token, Action wait)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Tick();
                wait();
            }
        }
        finally
        {
            lock (_gate)
            {
                try { _virtual.Submit(PadState.Neutral); _lastEmitted = PadState.Neutral; }
                catch (Exception e) { Trace.TraceError($"Could not send neutral on shutdown: {e.Message}"); }
            }
        }
    }

    private void Emit(PadState state)
    {
        if (state == _lastEmitted) return;
        _virtual.Submit(state);
        _lastEmitted = state;
    }

    // Re-checked every tick because the physical pad can move slots after a replug.
    private void EnsureNoLoop()
    {
        if (_physical.Slot == _virtual.Slot)
            throw new InvalidOperationException($"Refusing to read slot {_physical.Slot}: it is our own virtual pad.");
    }
}

public sealed record LoopbackResult(bool Passed, int Steps, IReadOnlyList<string> Failures);

/// <summary>Proves the emit path: what we submit must come back unchanged through the OS input API.</summary>
public static class LoopbackVerifier
{
    public static IEnumerable<PadState> Pattern()
    {
        foreach (var button in PadButtons.All) yield return new PadState { Buttons = button };
        yield return new PadState { LeftTrigger = 255 };
        yield return new PadState { RightTrigger = 128 };
        foreach (var v in new short[] { short.MaxValue, short.MinValue, 12345 })
        {
            yield return new PadState { LeftX = v };
            yield return new PadState { LeftY = v };
            yield return new PadState { RightX = v };
            yield return new PadState { RightY = v };
        }
        yield return new PadState(PadButtons.A | PadButtons.DpadLeft, 10, 20, 1000, -2000, 3000, -4000);
        yield return PadState.Neutral;
    }

    public static LoopbackResult Run(IVirtualPad pad, Func<PadState?> readBack, TimeSpan stepTimeout, Action wait)
    {
        var failures = new List<string>();
        var steps = 0;
        foreach (var expected in Pattern())
        {
            steps++;
            pad.Submit(expected);
            var timer = Stopwatch.StartNew();
            PadState? seen;
            do
            {
                seen = readBack();
                if (seen == expected) break;
                wait();
            } while (timer.Elapsed < stepTimeout);

            if (seen != expected) failures.Add($"sent {expected}, read back {seen?.ToString() ?? "nothing"}");
        }
        return new LoopbackResult(failures.Count == 0, steps, failures);
    }
}
