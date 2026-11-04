using Nefarius.ViGEm.Client.Targets.Xbox360;
using StickFix.Core;
using Xunit;

namespace StickFix.Tests;

internal sealed class FakePhysical : IPhysicalPad
{
    public int Slot { get; set; }
    public PadState? State { get; set; } = PadState.Neutral;

    public bool TryRead(out PadState state)
    {
        state = State ?? default;
        return State is not null;
    }
}

internal sealed class FakeVirtual : IVirtualPad
{
    public int Slot { get; set; } = 1;
    public List<PadState> Sent { get; } = [];
    public bool FailNext { get; set; }
    public bool Disposed { get; private set; }

    public void Submit(PadState state)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new IOException("bus gone");
        }
        Sent.Add(state);
    }

    public void Dispose() => Disposed = true;
}

public class ForwardingEngineTests
{
    private static readonly PadState Held = new(PadButtons.A | PadButtons.DpadUp, 200, 100, 1000, -2000, 3000, -4000);

    [Fact]
    public void Raw_mode_forwards_every_field_unchanged()
    {
        var virt = new FakeVirtual();
        new ForwardingEngine(new FakePhysical { State = Held }, virt, null).Tick();
        Assert.Equal([Held], virt.Sent);
    }

    [Fact]
    public void Identity_correction_changes_no_buttons_or_triggers_and_sticks_stay_within_one_count()
    {
        var virt = new FakeVirtual();
        new ForwardingEngine(new FakePhysical { State = Held }, virt, new PadCorrector(new(), new())).Tick();
        var sent = virt.Sent.Single();
        Assert.Equal(Held.Buttons, sent.Buttons);
        Assert.Equal(Held.LeftTrigger, sent.LeftTrigger);
        Assert.Equal(Held.RightTrigger, sent.RightTrigger);
        Assert.InRange(sent.LeftX - Held.LeftX, -1, 1);
        Assert.InRange(sent.RightY - Held.RightY, -1, 1);
    }

    [Fact]
    public void Correction_is_actually_what_gets_emitted()
    {
        var corrector = new PadCorrector(new StickSettings { DeadzoneInner = 0.5 }, new StickSettings());
        var virt = new FakeVirtual();
        new ForwardingEngine(new FakePhysical { State = Held }, virt, corrector).Tick();
        Assert.Equal(0, virt.Sent.Single().LeftX);
        Assert.Equal(0, virt.Sent.Single().LeftY);
    }

    [Fact]
    public void Bypass_emits_raw_while_a_corrector_is_loaded()
    {
        var virt = new FakeVirtual();
        var engine = new ForwardingEngine(new FakePhysical { State = Held }, virt, new PadCorrector(new StickSettings { DeadzoneInner = 0.5 }, new StickSettings())) { Bypass = true };
        engine.Tick();
        Assert.Equal(Held, virt.Sent.Single());
    }

    [Fact]
    public void Disconnect_releases_held_inputs_immediately()
    {
        var physical = new FakePhysical { State = Held };
        var virt = new FakeVirtual();
        var engine = new ForwardingEngine(physical, virt, null);
        engine.Tick();

        physical.State = null;
        engine.Tick();

        Assert.Equal(PadState.Neutral, virt.Sent[^1]);
        Assert.False(engine.Snapshot().Connected);
    }

    [Fact]
    public void Reconnect_resumes_forwarding()
    {
        var physical = new FakePhysical { State = Held };
        var virt = new FakeVirtual();
        var engine = new ForwardingEngine(physical, virt, null);
        engine.Tick();
        physical.State = null;
        engine.Tick();
        physical.State = Held;
        engine.Tick();
        Assert.Equal(Held, virt.Sent[^1]);
        Assert.True(engine.Snapshot().Connected);
    }

    [Fact]
    public void Unchanged_state_is_not_resubmitted()
    {
        var virt = new FakeVirtual();
        var engine = new ForwardingEngine(new FakePhysical { State = Held }, virt, null);
        engine.Tick();
        engine.Tick();
        Assert.Single(virt.Sent);
    }

    [Fact]
    public void Refuses_to_read_its_own_virtual_slot_at_start() =>
        Assert.Throws<InvalidOperationException>(() =>
            new ForwardingEngine(new FakePhysical { Slot = 1 }, new FakeVirtual { Slot = 1 }, null));

    [Fact]
    public void Refuses_if_the_physical_pad_later_moves_onto_the_virtual_slot()
    {
        var physical = new FakePhysical { Slot = 0 };
        var engine = new ForwardingEngine(physical, new FakeVirtual { Slot = 1 }, null);
        physical.Slot = 1;
        Assert.Throws<InvalidOperationException>(engine.Tick);
    }

    [Fact]
    public void Cancelling_a_run_leaves_the_pad_neutral()
    {
        var virt = new FakeVirtual();
        var engine = new ForwardingEngine(new FakePhysical { State = Held }, virt, null);
        using var cancel = new CancellationTokenSource();
        engine.Run(cancel.Token, cancel.Cancel);
        Assert.Equal(Held, virt.Sent[0]);
        Assert.Equal(PadState.Neutral, virt.Sent[^1]);
    }

    [Fact]
    public void A_fault_during_a_run_still_sends_neutral_and_surfaces_the_error()
    {
        var virt = new FakeVirtual { FailNext = true };
        var engine = new ForwardingEngine(new FakePhysical { State = Held }, virt, null);
        Assert.Throws<IOException>(() => engine.Run(CancellationToken.None, () => { }));
        Assert.Equal(PadState.Neutral, virt.Sent[^1]);
    }
}

public class LoopbackVerifierTests
{
    [Fact]
    public void Pattern_covers_every_button_and_extreme_and_ends_neutral()
    {
        var pattern = LoopbackVerifier.Pattern().ToList();
        foreach (var button in PadButtons.All) Assert.Contains(pattern, s => s.Buttons == button);
        Assert.Contains(pattern, s => s.LeftX == short.MinValue);
        Assert.Contains(pattern, s => s.RightY == short.MaxValue);
        Assert.Contains(pattern, s => s.LeftTrigger == 255);
        Assert.Contains(pattern, s => s.RightTrigger != 0);
        Assert.Equal(PadState.Neutral, pattern[^1]);
    }

    [Fact]
    public void Passes_when_state_echoes_back()
    {
        var virt = new FakeVirtual();
        var result = LoopbackVerifier.Run(virt, () => virt.Sent.LastOrDefault(), TimeSpan.FromMilliseconds(5), () => { });
        Assert.True(result.Passed);
    }

    [Fact]
    public void Fails_when_the_virtual_pad_never_updates()
    {
        // This is the prototype's symptom: the device exists, but nothing moves.
        var result = LoopbackVerifier.Run(new FakeVirtual(), () => PadState.Neutral, TimeSpan.FromMilliseconds(1), () => { });
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Failures);
    }

    [Fact]
    public void Fails_when_a_mapping_is_crossed()
    {
        var virt = new FakeVirtual();
        var result = LoopbackVerifier.Run(virt,
            () => virt.Sent[^1] with { LeftX = virt.Sent[^1].LeftY, LeftY = virt.Sent[^1].LeftX },
            TimeSpan.FromMilliseconds(1), () => { });
        Assert.False(result.Passed);
    }
}

public class ButtonMappingTests
{
    public static TheoryData<Xbox360Button, ushort> Pairs => new()
    {
        { Xbox360Button.Up, PadButtons.DpadUp },
        { Xbox360Button.Down, PadButtons.DpadDown },
        { Xbox360Button.Left, PadButtons.DpadLeft },
        { Xbox360Button.Right, PadButtons.DpadRight },
        { Xbox360Button.Start, PadButtons.Start },
        { Xbox360Button.Back, PadButtons.Back },
        { Xbox360Button.LeftThumb, PadButtons.LeftThumb },
        { Xbox360Button.RightThumb, PadButtons.RightThumb },
        { Xbox360Button.LeftShoulder, PadButtons.LeftShoulder },
        { Xbox360Button.RightShoulder, PadButtons.RightShoulder },
        { Xbox360Button.Guide, PadButtons.Guide },
        { Xbox360Button.A, PadButtons.A },
        { Xbox360Button.B, PadButtons.B },
        { Xbox360Button.X, PadButtons.X },
        { Xbox360Button.Y, PadButtons.Y },
    };

    // ViGEmPad.Submit passes the bits straight through, so this is what keeps that safe.
    [Theory]
    [MemberData(nameof(Pairs))]
    public void ViGEm_bits_equal_XInput_bits(Xbox360Button vigem, ushort xinput) =>
        Assert.Equal(xinput, vigem.Value);
}
