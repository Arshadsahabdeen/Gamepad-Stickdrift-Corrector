# Architecture

## Components

```
Real controller --XInput--> IPhysicalPad --> PadCorrector --> IVirtualPad --ViGEm--> Virtual X360 pad --> Game
                                                                                          |
                                         read-back check <--XInput on the virtual slot----+
```

| Concern | Where | Notes |
|---|---|---|
| Read | `XInputPad` | Polls only the selected slot. Polling empty slots is slow, so it rescans once a second |
| Correct | `StickFix.Core` | Pure maths with no OS calls, fully unit-tested |
| Emit | `ViGEmPad` | Submits on change. Neutral on stop, disconnect and fault |
| Hide | `HidingGuard`, `HidHideDriver` | Journaled so every change can be undone |
| Feedback-loop guard | `ForwardingEngine` | Refuses to run if the physical slot equals the virtual slot |

One unelevated app runs the engine on its own thread, with the UI polling at 30 Hz (4 Hz in the tray). Neither driver
needs admin rights at runtime. Only installing them does.

## Technology choices

| Choice | Why |
|---|---|
| C# / .NET 10, WPF | Good libraries for both drivers. A real `.exe` image path also means HidHide's allow-list matches exactly |
| XInput for reading | Built in, gives unfiltered 16-bit axes. Limit: Xbox-style pads only |
| ViGEmBus for output | Signed and widely deployed, but **retired upstream since 2023**. Output sits behind `IVirtualPad` so another backend such as [VIIPER](https://github.com/Alia5/VIIPER) can replace it |
| HidHide for hiding | Maintained, MIT licensed, no admin needed to configure |
| Inno Setup | Simple driver-setup chaining. MSIX is a poor fit for bundling drivers |

## Lessons from real hardware

Tested on a PowerA `20D6:2005`, which uses Windows' Xbox GIP driver:

- **ViGEm's `UserIndex` is wrong here.** It reported slot 0 while the real pad held slot 0, because it counts only XUSB devices. The virtual pad's slot is found by emitting a tiny marker and seeing which slot shows it.
- **The USB serial changes between connections**, so profiles are keyed by vendor and product ID only.
- **XInput reports a generic `045E:02FF`** for this pad. The real IDs come from walking the device tree up to the USB node.
- **Hiding needs all three nodes:** `HID\...`, `USB\...&IG_00\...` and `USB\VID_20D6...`.
- **Hiding doesn't affect open handles.** A process that opened the pad before hiding keeps reading it, so games must start after StickFix.
- **The stick has no noise at rest, yet settles in different places after moving** (left Y seen between 0.17 and 0.22). Calibration therefore averages several rests and sizes the deadzone from their spread.

## Correction maths

Values are normalised with +Y up: `n = v/32767` for `v >= 0`, otherwise `v/32768`, which round-trips exactly.

Per axis, with centre `c` and separately measured half-ranges `r+` and `r-`:

```
u = (x - c) / r+     if x >= c
u = (x - c) / r-     otherwise          then clamp to [-1, 1]
```

Radial shaping on `(ux, uy)` preserves direction, where per-axis deadzones would snap diagonals to the axes:

```
m   = |u|
m'  = 0                                 if m <= dIn
m'  = min(1, (m - dIn) / (dOut - dIn))  otherwise (rescaled, so no jump at the edge)
m'' = min(1, sensitivity * m'^gamma)
out = (u / m) * m''
```

Hysteresis: at rest, the stick must exceed `dIn + h` to wake, and it rests again below `dIn`. This stops chatter at
the boundary and adds no latency.

Filtering is off by default. The option is a One Euro filter, which smooths slow motion and adds little lag to fast
motion, unlike a moving average or fixed EMA.

**Calibration:** `c` is the average of the rest medians across three measurements, with the stick moved in between.

```
dIn = clamp(k * (r99 + returnScatter), 0.02 * k, 0.25)
```

`r99` is the 99th-percentile distance from centre at rest, `returnScatter` is the largest distance between rest
medians, and `k` is 1.2 minimal, 1.5 balanced, 2.5 strong. Above 0.12 the app warns about lost precision. Above 0.25
it refuses to apply without an explicit override. These thresholds are starting values from one controller.

## Crash safety

Hiding is a persistent system change that outlives the app:

1. The journal in `%LOCALAPPDATA%\StickFix\hiding.json` is written **before** any change, recording only what StickFix added.
2. The app is allowed before the pad is blocked. Hiding is locked until the virtual pad has been verified.
3. If the app can't read the pad after hiding, hiding is undone.
4. A watchdog process (`StickFix.exe --watchdog <pid>`) restores visibility when the app exits for any reason.
5. An HKCU Run entry (`--recover`) covers power loss, and is removed once visibility is restored.
6. The uninstaller runs `--recover` before deleting the program.
