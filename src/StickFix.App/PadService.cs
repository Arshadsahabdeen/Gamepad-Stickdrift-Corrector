using System.Diagnostics;
using System.IO;
using StickFix.Core;
using StickFix.Windows;

namespace StickFix.App;

public sealed record PadView(
    bool Connected, string ControllerName, PadState Raw, PadState Output, PadState? VirtualSeen,
    bool Running, int VirtualSlot, bool CorrectionOn, bool Hidden, int Mismatches, string? Problem, bool DriverMissing);

/// <summary>Owns the engine, virtual pad and hiding for the whole app. All public members run on the UI thread.</summary>
public sealed class PadService : IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private HighResTimer? _timer;
    private ViGEmPad? _pad;
    private XInputPad? _physical;
    private ForwardingEngine? _engine;
    private Thread? _worker;
    private CancellationTokenSource? _cancel;
    private volatile Exception? _fault;

    private int? _idleSlot;
    private long _nextScan;
    private bool _wasConnected, _userStopped, _correctionOn = true;
    private PadState _lastOutput;
    private int _mismatches;
    private string? _problem;
    private bool _driverMissing;
    private PhysicalDevice? _device;

    public Profile? Profile { get; private set; }
    public string ControllerId { get; private set; } = "";
    public bool HideFromGames => _settings.HideFromGames;
    public bool IsRunning => _engine is not null;
    public bool HidHideInstalled { get; private set; }

    public PadService()
    {
        RefreshHidHide();
        // A previous run may have died while hiding, so always start from a clean slate.
        if (File.Exists(Paths.Journal)) Recovery.RestoreNow();
    }

    public void RefreshHidHide()
    {
        try
        {
            HidHideInstalled = new HidHideDriver().IsInstalled;
        }
        catch (Exception e)
        {
            Log.Write($"HidHide check failed: {e.Message}");
            HidHideInstalled = false;
        }
    }

    /// <summary>Called about 30 times a second by the window. Also does auto-start and reconnect handling.</summary>
    public PadView Poll()
    {
        if (_fault is { } fault)
        {
            _fault = null;
            Log.Write($"Engine fault: {fault}");
            Stop();
            _problem = $"Correction stopped because of an error: {fault.Message}";
        }
        return _engine is null ? PollIdle() : PollRunning();
    }

    private PadView PollIdle()
    {
        var connected = false;
        var raw = PadState.Neutral;
        if (_idleSlot is { } slot) connected = XInput.TryRead(slot, out raw);

        if (!connected && Environment.TickCount64 >= _nextScan)
        {
            _nextScan = Environment.TickCount64 + 1000;
            _idleSlot = XInput.Scan().FirstOrDefault()?.Slot;
            if (_idleSlot is { } found) connected = XInput.TryRead(found, out raw);
        }

        if (connected && !_wasConnected) OnConnected();
        _wasConnected = connected;

        if (connected && !_userStopped && Profile is not null && _problem is null) Start();

        return new PadView(connected, Name, raw, raw, null, false, -1, _correctionOn, false, 0, _problem, _driverMissing);
    }

    private PadView PollRunning()
    {
        var (connected, raw, output) = _engine!.Snapshot();
        PadState? seen = XInput.TryRead(_pad!.Slot, out var state) ? state : null;

        // Only counts when the output held still since last poll, otherwise it's just timing.
        if (output == _lastOutput && seen != output) _mismatches++;
        _lastOutput = output;

        if (connected && !_wasConnected)
        {
            OnConnected();
            // A replugged pad can return with new instance IDs, which would be visible to games again.
            if (GuardEngaged) TryHide();
        }
        _wasConnected = connected;

        return new PadView(connected, Name, raw, output, seen, true, _pad.Slot, _correctionOn && Profile is not null,
            GuardEngaged, _mismatches, _problem, false);
    }

    private string Name => _device?.DisplayName ?? "Controller";

    private bool GuardEngaged => File.Exists(Paths.Journal);

    private void OnConnected()
    {
        try
        {
            // shortcut: uses the first physical pad only; add a controller picker when multi-pad use matters.
            _device = DeviceTree.FindPhysical().FirstOrDefault();
        }
        catch (Exception e)
        {
            Log.Write($"Device lookup failed: {e.Message}");
            _device = null;
        }

        // A pad that's hidden, or has no USB chain we recognise, must not lose its calibration to a lookup miss.
        if (_device is null && Profile is not null) return;
        var id = _device?.ControllerId ?? "unknown";
        if (id == ControllerId && Profile is not null) return;
        ControllerId = id;
        Profile = LoadProfile(id);
        _engine?.SetCorrector(CorrectorFor(Profile));
    }

    private Profile? LoadProfile(string id)
    {
        var path = ProfileStore.PathFor(ProfileStore.DefaultDirectory, id, "Default");
        AdoptSerialNamedProfile(id, path);
        if (!File.Exists(path)) return null;
        try
        {
            return ProfileStore.Load(path);
        }
        catch (ProfileException e)
        {
            _problem = e.Message;
            Log.Write(e.Message);
            return null;
        }
    }

    // Earlier builds put the USB serial in the file name. Keep the newest such calibration rather than lose it.
    private static void AdoptSerialNamedProfile(string id, string path)
    {
        if (File.Exists(path) || !Directory.Exists(ProfileStore.DefaultDirectory)) return;
        var newest = new DirectoryInfo(ProfileStore.DefaultDirectory).GetFiles($"{id}-*-Default.json")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        newest?.MoveTo(path);
    }

    private PadCorrector? CorrectorFor(Profile? profile) =>
        profile is not null && _correctionOn ? new PadCorrector(profile.Left, profile.Right) : null;

    public void UserStart()
    {
        _userStopped = false;
        _problem = null;
        Start();
    }

    public void UserStop()
    {
        _userStopped = true;
        Stop();
    }

    private void Start()
    {
        if (_engine is not null) return;
        _problem = null;
        _driverMissing = false;
        try
        {
            _timer = new HighResTimer();
            _pad = ViGEmPad.Create();
            var others = XInput.Scan().Where(p => p.Slot != _pad.Slot).ToList();
            if (others.Count == 0) throw new VirtualPadException("No controller found. Plug one in and try again.");
            _physical = new XInputPad(others[0].Slot, true, others.Skip(1).Select(p => p.Slot).Append(_pad.Slot).ToHashSet());

            var physical = _physical;
            _pad.Rumble += (large, small) => XInput.SetRumble(physical.Slot, large, small);

            _engine = new ForwardingEngine(_physical, _pad, CorrectorFor(Profile));
            _cancel = new CancellationTokenSource();
            var engine = _engine;
            var token = _cancel.Token;
            _worker = new Thread(() =>
            {
                try { engine.Run(token, () => Thread.Sleep(1)); }
                catch (Exception e) { _fault = e; }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "StickFix engine" };
            _worker.Start();

            _mismatches = 0;
            _wasConnected = true;
            Log.Write($"Started: physical slot {_physical.Slot}, virtual slot {_pad.Slot}, correction {(Profile is null ? "none" : "profile")}.");

            if (_settings.HideFromGames) TryHide();
        }
        catch (VirtualPadException e)
        {
            _driverMissing = e.Message.Contains("ViGEmBus");
            _problem = e.Message;
            Log.Write(e.Message);
            Stop(keepProblem: true);
        }
    }

    public void Stop(bool keepProblem = false)
    {
        RestoreHiding();
        _cancel?.Cancel();
        _worker?.Join(2000);
        _pad?.Dispose();
        _timer?.Dispose();
        _cancel?.Dispose();
        _engine = null;
        _pad = null;
        _physical = null;
        _worker = null;
        _cancel = null;
        _timer = null;
        _idleSlot = null;
        _nextScan = 0;
        _wasConnected = false;
        if (!keepProblem) _problem = null;
    }

    public void SetCorrection(bool on)
    {
        _correctionOn = on;
        _engine?.SetCorrector(CorrectorFor(Profile));
    }

    public void SetProfile(Profile profile)
    {
        var problem = profile.Left.Validate() ?? profile.Right.Validate();
        if (problem is not null) throw new ProfileException($"Those settings can't be used: {problem}.");

        ProfileStore.Save(profile, ProfileStore.PathFor(ProfileStore.DefaultDirectory, ControllerId, "Default"));
        Profile = profile;
        _engine?.SetCorrector(CorrectorFor(profile));
        _problem = null;
    }

    public void DeleteProfile()
    {
        var path = ProfileStore.PathFor(ProfileStore.DefaultDirectory, ControllerId, "Default");
        if (File.Exists(path)) File.Delete(path);
        Profile = null;
        _engine?.SetCorrector(null);
    }

    /// <summary>Turns hiding on or off. Returns a message for the user when something needs explaining.</summary>
    public string? SetHide(bool on)
    {
        _settings.HideFromGames = on;
        _settings.Save();
        if (!on)
        {
            RestoreHiding();
            return null;
        }
        return _engine is null ? null : TryHide();
    }

    private string? TryHide()
    {
        var message = HideCore();
        if (message is not null) Log.Write($"Hiding: {message}");
        return message;
    }

    private string? HideCore()
    {
        // Never hide the real pad until the virtual one has proven it works.
        if (_pad is null || _mismatches > 0) return "The virtual controller isn't verified, so the real one stays visible.";

        RefreshHidHide();
        if (!HidHideInstalled) return "HidHide isn't installed, so the real controller can't be hidden.";

        try
        {
            var devices = DeviceTree.FindPhysical();
            if (devices.Count != 1)
                return devices.Count == 0
                    ? "This controller type can't be hidden yet, so games will still see it."
                    : "Several controllers are connected. Unplug the others to use hiding.";

            StartWatchdogOnce();
            StartupEntry.SetRecovery(true);
            Recovery.Guard(new HidHideDriver()).Hide(devices[0].InstanceIds, Paths.Exe);
            Log.Write($"Hidden: {string.Join(", ", devices[0].InstanceIds)}");

            // If we can't read it now, hiding has broken forwarding. Undo it rather than leave a dead controller.
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 500)
            {
                if (_physical is { } physical && XInput.TryRead(physical.Slot, out _)) return null;
                Thread.Sleep(20);
            }
            RestoreHiding();
            return "After hiding, StickFix couldn't read the controller, so hiding was undone.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"Hide failed: {e}");
            RestoreHiding();
            return $"Couldn't hide the controller: {e.Message} If the HidHide app is open, close it and try again.";
        }
    }

    private bool _watchdogStarted;

    private void StartWatchdogOnce()
    {
        if (_watchdogStarted) return;
        Process.Start(new ProcessStartInfo(Paths.Exe, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false });
        _watchdogStarted = true;
    }

    public void RestoreHiding()
    {
        if (!GuardEngaged) return;
        Recovery.RestoreNow();
    }

    public LoopbackResult RunVirtualTest()
    {
        var wasRunning = IsRunning;
        Stop();
        try
        {
            using var timer = new HighResTimer();
            using var pad = ViGEmPad.Create();
            return LoopbackVerifier.Run(pad, () => XInput.TryRead(pad.Slot, out var s) ? s : null,
                TimeSpan.FromMilliseconds(250), () => Thread.Sleep(1));
        }
        finally
        {
            if (wasRunning) Start();
        }
    }

    public string BuildReport()
    {
        var lines = new List<string>
        {
            $"StickFix report, {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Windows: {Environment.OSVersion}",
            $"Running: {IsRunning}, hidden: {GuardEngaged}, HidHide installed: {HidHideInstalled}, mismatches: {_mismatches}",
            $"Controller id: {ControllerId}",
            "XInput slots:",
        };
        lines.AddRange(XInput.Scan().Select(p => $"  slot {p.Slot}: {p.ControllerId}"));
        lines.Add("Device chains:");
        try
        {
            lines.AddRange(DeviceTree.FindPhysical().Select(d => $"  {d.DisplayName}: {string.Join(" <- ", d.InstanceIds)}"));
        }
        catch (Exception e)
        {
            lines.Add($"  lookup failed: {e.Message}");
        }
        if (Profile is { } p)
        {
            lines.Add($"Profile: created {p.CreatedUtc:u}, confidence {p.Confidence}, left {p.LeftFindings}, right {p.RightFindings}");
            lines.Add($"  left center ({p.Left.X.Center:0.0000}, {p.Left.Y.Center:0.0000}) deadzone {p.Left.DeadzoneInner:0.000}");
            lines.Add($"  right center ({p.Right.X.Center:0.0000}, {p.Right.Y.Center:0.0000}) deadzone {p.Right.DeadzoneInner:0.000}");
        }
        if (File.Exists(Paths.LogFile))
        {
            lines.Add("Recent log:");
            lines.AddRange(File.ReadLines(Paths.LogFile).TakeLast(50));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose() => Stop();
}
