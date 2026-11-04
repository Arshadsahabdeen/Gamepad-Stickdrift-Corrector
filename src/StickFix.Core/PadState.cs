namespace StickFix.Core;

/// <summary>One gamepad snapshot in XInput layout: +Y is up, buttons use XUSB bit flags.</summary>
public readonly record struct PadState(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY)
{
    public static readonly PadState Neutral = default;
}

public static class PadButtons
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public static readonly ushort[] All =
    [
        DpadUp, DpadDown, DpadLeft, DpadRight, Start, Back, LeftThumb, RightThumb,
        LeftShoulder, RightShoulder, Guide, A, B, X, Y,
    ];
}

public static class Axis
{
    // Asymmetric on purpose: short spans -32768..32767, and this makes ToShort(ToUnit(v)) == v for every v.
    public static double ToUnit(short v) => v >= 0 ? v / 32767.0 : v / 32768.0;

    public static short ToShort(double u)
    {
        if (double.IsNaN(u)) return 0;
        u = Math.Clamp(u, -1.0, 1.0);
        return (short)Math.Round(u >= 0 ? u * 32767.0 : u * 32768.0);
    }
}
