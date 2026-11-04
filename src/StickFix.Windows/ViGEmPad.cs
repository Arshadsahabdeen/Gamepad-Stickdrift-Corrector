using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using StickFix.Core;

namespace StickFix.Windows;

public sealed class VirtualPadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Virtual Xbox 360 pad. XUSB button bits equal XInput's, so buttons pass through untouched.</summary>
public sealed class ViGEmPad : IVirtualPad
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;
    private bool _disposed;

    public int Slot { get; private set; }

    public event Action<byte, byte>? Rumble;

    private ViGEmPad(ViGEmClient client, IXbox360Controller pad)
    {
        _client = client;
        _pad = pad;
        _pad.FeedbackReceived += (_, e) => Rumble?.Invoke(e.LargeMotor, e.SmallMotor);
    }

    public static ViGEmPad Create()
    {
        ViGEmClient client;
        try
        {
            client = new ViGEmClient();
        }
        catch (VigemBusNotFoundException e)
        {
            throw new VirtualPadException("The ViGEmBus driver is not installed, so no virtual controller can be created.", e);
        }
        catch (DllNotFoundException e)
        {
            throw new VirtualPadException("The ViGEm client library (vigemclient.dll) is missing from the app folder.", e);
        }

        IXbox360Controller? pad = null;
        try
        {
            pad = client.CreateXbox360Controller();
            pad.AutoSubmitReport = false;
            pad.Connect();
            var created = new ViGEmPad(client, pad);
            created.Slot = LoopbackVerifier.FindSlot(
                    created.Submit,
                    slot => XInput.TryRead(slot, out var state) ? state : null,
                    XInput.SlotCount,
                    TimeSpan.FromSeconds(3),
                    () => Thread.Sleep(10))
                ?? throw new VirtualPadException("The virtual controller was created but its output never appeared on any XInput slot. All four slots may be in use.");
            return created;
        }
        catch (Exception e)
        {
            try { pad?.Disconnect(); } catch (VigemTargetNotPluggedInException) { }
            client.Dispose();
            throw e as VirtualPadException ?? new VirtualPadException($"Could not create the virtual controller: {e.Message}", e);
        }
    }

    public void Submit(PadState s)
    {
        _pad.SetButtonsFull(s.Buttons);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftX);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftY);
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RightX);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RightY);
        _pad.SubmitReport();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Submit(PadState.Neutral);
            _pad.Disconnect();
        }
        catch (Exception e)
        {
            Trace.TraceError($"Virtual pad shutdown: {e.Message}");
        }
        _client.Dispose();
    }
}
