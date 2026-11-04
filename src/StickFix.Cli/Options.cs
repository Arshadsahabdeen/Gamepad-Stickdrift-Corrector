using StickFix.Core;
using StickFix.Windows;

namespace StickFix.Cli;

public sealed class UsageException(string message) : Exception(message);

public sealed class Options(string[] args)
{
    public bool Has(string flag) => args.Contains(flag);

    public string? Value(string flag)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0) return null;
        return i + 1 < args.Length ? args[i + 1] : throw new UsageException($"{flag} needs a value.");
    }

    public int? Slot
    {
        get
        {
            if (Value("--slot") is not { } text) return null;
            return int.TryParse(text, out var slot) && slot is >= 0 and < XInput.SlotCount
                ? slot
                : throw new UsageException("--slot must be 0 to 3.");
        }
    }

    public CorrectionStrength Strength => Value("--strength") switch
    {
        null or "balanced" => CorrectionStrength.Balanced,
        "minimal" => CorrectionStrength.Minimal,
        "strong" => CorrectionStrength.Strong,
        _ => throw new UsageException("--strength must be minimal, balanced or strong."),
    };

    /// <summary>Picks a physical pad. Must run before our virtual pad exists, so it can't pick itself.</summary>
    public SlotInfo PickPhysical()
    {
        var pads = XInput.Scan();
        if (Slot is { } slot)
            return pads.FirstOrDefault(p => p.Slot == slot) ?? throw new UsageException($"No controller on slot {slot}.");

        return pads.Count switch
        {
            0 => throw new UsageException("No XInput controller found. Connect one, or if it's a non-Xbox pad, note that only XInput is supported so far."),
            1 => pads[0],
            _ => throw new UsageException($"Several controllers found (slots {string.Join(", ", pads.Select(p => p.Slot))}). Choose one with --slot N. If one is a virtual pad from other software, pick the other."),
        };
    }
}
