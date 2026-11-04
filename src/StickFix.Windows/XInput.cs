using System.Diagnostics;
using System.Runtime.InteropServices;
using StickFix.Core;

namespace StickFix.Windows;

public sealed record SlotInfo(int Slot, ushort? VendorId, ushort? ProductId)
{
    public string ControllerId => VendorId is { } v && ProductId is { } p ? $"{v:X4}-{p:X4}" : "unknown";
}

public static class XInput
{
    public const int SlotCount = 4;

    /// <summary>Polling an empty slot is slow, so scan rarely and never from a hot path.</summary>
    public static IReadOnlyList<SlotInfo> Scan()
    {
        var found = new List<SlotInfo>();
        for (var slot = 0; slot < SlotCount; slot++)
            if (TryRead(slot, out _)) found.Add(Info(slot));
        return found;
    }

    public static bool TryRead(int slot, out PadState state)
    {
        // shortcut: ordinal 100 (undocumented, stable since Win7) is the only way to see the Guide button.
        if (XInputGetStateEx((uint)slot, out var s) != 0)
        {
            state = default;
            return false;
        }
        var g = s.Gamepad;
        state = new PadState(g.Buttons, g.LeftTrigger, g.RightTrigger, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY);
        return true;
    }

    public static void SetRumble(int slot, byte large, byte small)
    {
        // XInput motors are 16-bit, ViGEm feedback is 8-bit: 0xFF * 257 = 0xFFFF.
        var v = new XInputVibration { Left = (ushort)(large * 257), Right = (ushort)(small * 257) };
        _ = XInputSetState((uint)slot, ref v);
    }

    private static SlotInfo Info(int slot)
    {
        try
        {
            // shortcut: ordinal 108 is undocumented. It's used only for identity, so failure just means "unknown".
            if (XInputGetCapabilitiesEx(1, (uint)slot, 0, out var caps) == 0)
                return new SlotInfo(slot, caps.VendorId, caps.ProductId);
        }
        catch (EntryPointNotFoundException)
        {
        }
        return new SlotInfo(slot, null, null);
    }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    private static extern uint XInputGetCapabilitiesEx(uint unknown, uint userIndex, uint flags, out XInputCapabilitiesEx caps);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left, Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesEx
    {
        public byte Type, SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
        public ushort VendorId, ProductId, VersionNumber, Unknown1;
        public uint Unknown2;
    }
}

public sealed class PadDisconnectedException() : Exception("The controller disconnected during measurement. Reconnect it and try again.");

public static class Sampler
{
    public const double RateHz = 125;

    /// <summary>Reads a slot at a steady rate. Throws PadDisconnectedException if the pad goes away.</summary>
    public static void Run(int slot, TimeSpan duration, Action<PadState> onSample, CancellationToken token = default)
    {
        using var timer = new HighResTimer();
        var clock = Stopwatch.StartNew();
        long taken = 0;
        while (clock.Elapsed < duration)
        {
            token.ThrowIfCancellationRequested();
            if (!XInput.TryRead(slot, out var state)) throw new PadDisconnectedException();
            onSample(state);
            taken++;
            var due = TimeSpan.FromSeconds(taken / RateHz) - clock.Elapsed;
            if (due > TimeSpan.Zero) Thread.Sleep(due);
        }
    }
}

/// <summary>
/// A physical pad on an XInput slot. Windows may assign a different slot after a replug, so with
/// allowMove the pad re-finds itself once a second, skipping excluded slots (other pads, our virtual pad).
/// </summary>
public sealed class XInputPad(int slot, bool allowMove, IReadOnlySet<int> excluded) : IPhysicalPad
{
    private long _nextScan;

    public int Slot { get; private set; } = slot;

    public bool TryRead(out PadState state)
    {
        if (XInput.TryRead(Slot, out state)) return true;
        if (!allowMove || Environment.TickCount64 < _nextScan) return false;

        _nextScan = Environment.TickCount64 + 1000;
        for (var s = 0; s < XInput.SlotCount; s++)
        {
            if (excluded.Contains(s) || !XInput.TryRead(s, out state)) continue;
            Slot = s;
            return true;
        }
        return false;
    }
}
