# StickFix: technical review and architecture

Researched 2026-11-04. "Verified" means read on the source page that day. "Inferred" means my
engineering judgement, not yet tested on hardware.

## 1. Current prototype

`D:\Controller-drift-fix` is **not on this machine**, so I have not read the script. This PC also looks
different from the one described: it has Lenovo ACPI devices and an Intel chipset, while a G513QY is AMD.
No controller, ViGEmBus or HidHide is present here. Everything below about the prototype comes from your
description, not from its code.

Structural problems with the pygame + vgamepad approach, whatever the specific bug turns out to be:

- pygame/SDL picks its joystick backend and index itself, so it can't reliably tell "physical pad" from
  "my own virtual pad". After hiding, index 0 can silently become the virtual pad.
- A Python venv's `python.exe` is a launcher that starts the base interpreter (inferred from CPython
  behaviour). HidHide matches the on-disk image path, so allow-listing the venv path allows the wrong binary.
- Nothing measured whether the emitted state matched the intended state, so "appears in joy.cpl" was the
  only signal.

## 2. Why the virtual pad did not move: how to tell the causes apart

Test in this order. The first check that fails is the cause.

| # | Hypothesis | Distinguishing test |
|---|---|---|
| 1 | Virtual pad never receives updates (code never submits, or swallowed exception) | `stickfix selftest`: writes a pattern to the virtual pad and reads it back through XInput. Needs no physical controller |
| 2 | App reads nothing while not focused (SDL drops joystick events for an unfocused app unless `SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS` is set; inferred from SDL docs) | Print raw values with another window focused, HidHide off |
| 3 | Reading the wrong device, i.e. its own virtual pad, which stays neutral | Log which slot/device is read; `stickfix devices` before and after creating the virtual pad |
| 4 | Allow-list entry doesn't match the running binary (venv launcher) | `HidHideCLI --app-list` vs the actual process path in Task Manager |
| 5 | Stale device stack: the filter only attaches to stacks created after the driver registers (HidHide FAQ) | Replug the pad or reboot, then repeat |
| 6 | Wrong node hidden: XInput can open the `USB\...` base container and bypass the `HID\...` cloak (HidHide docs) | `--dev-list`: both instance paths must be listed |
| 7 | Tester mismatch (tester watches a different pad index or API) | Use `selftest`, which needs no third-party tester |

Sign trap on your numbers: browser testers report **+Y = down**, XInput reports **+Y = up**. So left-Y
-0.2096 in a browser tester would be about +0.21 (up) in XInput. If your reading came from such a tester,
that is the likely sign. Never hard-code it; measure it.

## 3. Options compared

### Reading the physical pad

| Option | Verdict |
|---|---|
| **XInput (`xinput1_4`)** | **First backend.** Documented, no install, unfiltered 16-bit axes, 4 stable slots, and the virtual pad's slot is known so it can be excluded. Limit: Xbox-style pads only, no serial number. Whether it still reads a hidden pad when allow-listed is **the top risk to test on hardware** |
| GameInput | Not now. Requires shipping a redistributable (verified), C++ API, and I found no docs on how it interacts with HidHide |
| Raw Input / HID (hidapi) | Second backend, for PowerA models that are not XInput (e.g. Switch-layout pads). Needs per-device report mapping |
| DirectInput | Legacy. Merges triggers on Xbox pads. Skip |
| SDL3 | Good fallback for broad device support, but it hides which API it used, which is exactly the ambiguity we need to avoid here |

### Virtual output

| Option | Status (verified) | Verdict |
|---|---|---|
| **ViGEmBus 1.22.0** + `Nefarius.ViGEm.Client` 1.21.256 | **Retired, repo archived Nov 2023, no more updates.** BSD-3 driver, MIT client. Production-signed installer for Win10/11. Silent install flag documented in old notes. EOL page says existing installs are unaffected | **First backend.** Dead but stable and widely deployed. Risk: a future Windows update could break it, with no upstream fix. Some users report BSODs (unverified, one GitHub issue) |
| VIIPER 0.9.0 + usbip-win2 0.9.8.1 | Active. **Pre-1.0.** Server GPL-3 (runs as a separate process), clients MIT, has a C# client. Install restarts all USB 3 hubs. The latest usbip-win2 release warns of possible regressions. A DS4Windows fork uses it (release candidate) | **Planned second backend** and the exit route from ViGEm. Too young to be the default today |
| VirtualPad (Nefarius) | Commercial, per one secondary source | Not evaluated |
| vJoy | DirectInput only | Games using XInput won't see it. No |
| Own VHF/KMDF driver | Requires driver signing, and a HID device doesn't appear as XUSB | No |

Because ViGEm has no future, output sits behind `IVirtualPad`, so VIIPER can be added without touching the
engine. **Reassess when VIIPER reaches 1.0.**

### Hiding the physical pad

**HidHide 1.5.230** is the only practical option: MIT, maintained, though the README says there is "no
capacity for any major works". Facts that shape the design (verified):

- Configuration does **not** need admin.
- `Nefarius.Drivers.HidHide` 3.4.0 (MIT, May 2026, net10 target) gives an API, so no CLI scraping.
- Only one process may hold `\\.\HidHide`, so the HidHide Configuration Client must be closed.
- XInput pads need **both** `HID\` and `USB\` instance paths hidden. The GUI can't do this reliably.
- **Raw Input cannot be blocked.** Games that read Raw Input may still see the physical pad.

### Language, UI, packaging

- **C# / .NET 10 (LTS)**: first-class NuGet packages for both drivers, self-contained publish means no runtime
  for users, and `dotnet test` is easy. Rust/C++ would bring no measurable latency gain at about 250 Hz polling,
  but would mean writing the driver bindings. Python is rejected (launcher/allow-list issue, packaging).
- **WPF**: mature, simple self-contained deployment, good canvas drawing for stick visuals, real keyboard
  and automation support. WinUI 3 adds packaging friction for no benefit here.
- **Inno Setup** for the installer: straightforward driver-installer chaining with clear prompts. Commercial
  use needs a paid licence; free for personal use (verified on jrsoftware.org). MSIX is a poor fit for driver
  bundling. WiX is the alternative; its fee terms were not verified.

## 4. Runtime architecture

```
Physical pad --XInput--> IPhysicalPad --> CorrectionPipeline --> IVirtualPad --ViGEm--> Virtual X360 pad --> Game
                                                                                           |
                                         LoopbackVerifier <--XInput on the virtual slot----+
```

The six concerns you listed stay separate:

1. **Read**: `XInputPad` polls only the selected slot. Polling empty slots is slow, so rescan once a second.
2. **Correct**: `StickFix.Core`, pure maths with no OS calls, fully unit-tested.
3. **Emit**: `ViGEmPad`. Submits on change. Sends neutral on stop, disconnect and fault.
4. **Hide**: `HidHideGuard`, Phase 4, with a guard rail (below).
5. **Game sees only corrected**: manual per-game test. Raw Input games are a known gap.
6. **No feedback loop**: the engine refuses to start if physical slot == virtual slot, and slot selection
   excludes the virtual pad's reported `UserIndex`.

**Verified means loopback.** A virtual pad counts as working only when the state read back through XInput on
its slot equals what was sent. `selftest` does this with no physical controller.

**Process model.** One unelevated tray app runs the engine on a dedicated thread. No Windows service, because
neither driver needs admin at runtime (HidHide verified; ViGEm inferred from other apps, to be confirmed).
Autostart uses the HKCU Run key. Only driver install elevates.

**Crash safety for hiding (Phase 4).** Hiding is a persistent system change that outlives the app, so:

- Before hiding, write a journal of what we changed, in `%LOCALAPPDATA%`.
- A tiny watchdog process waits on the app's process handle and, when it exits for any reason, restores
  visibility from the journal.
- At logon, if a journal exists and the engine isn't running, restore. This covers power loss.
- Hiding is locked until a loopback check has passed in this session.
- ViGEm should remove the virtual pad when the process dies (inferred; on the HIL test list).

## 5. Correction maths

All values are normalised with +Y up: `n = v/32767` for `v >= 0`, otherwise `v/32768`, which round-trips exactly.

Per-axis, with centre `c` and separately measured half-ranges `r+`, `r-`:

```
u = (x - c) / r+     if x >= c
u = (x - c) / r-     otherwise          then clamp to [-1, 1]
```

Separate half-ranges matter: a stick centred at -0.21 has about 0.79 of travel one way and 1.21 the other.

Radial shaping is applied to the pair `(ux, uy)` so direction is preserved (per-axis deadzones snap diagonals
to the axes):

```
m  = |u|
m' = 0                                        if m <= dIn
m' = min(1, (m - dIn) / (dOut - dIn))         otherwise      (rescaled, so no jump at the edge)
m'' = min(1, sensitivity * m'^gamma)
out = (u / m) * m''
```

Hysteresis: once at rest, the stick must exceed `dIn + h` to wake. It rests again below `dIn`. This stops
chatter at the boundary with no added latency.

Filtering: **off by default**.

| Strategy | Trade-off |
|---|---|
| Moving average | Fixed lag at all speeds. Bad for aim |
| Fixed EMA | Same lag problem, cheaper |
| **One Euro filter** (chosen, optional) | Smooths when slow, almost no lag when fast. Two parameters |
| Kalman | Needs a motion model. Not justified here |

Recommended inner deadzone: `dIn = clamp(k * (r99 + returnScatter), 0.02, 0.25)`, where `r99` is the
99th-percentile distance from centre at rest, and `k` is 1.2 minimal, 1.5 balanced, 2.5 strong. Above 0.12 we
warn about lost precision. Above the 0.25 cap we refuse to auto-apply. These thresholds are starting values and
need tuning against real sticks.

## 6. Limits software can't solve

- Worn potentiometers: if the rest position wanders more than a small deadzone, you must trade precision for
  no-drift. Replacing the stick module or using Hall-effect sticks is the real fix.
- Restricted range or a physically damaged stick can be rescaled, but with lost resolution.
- Anti-cheat may reject virtual pads, or even the drivers. Never promised to work.
- Games reading Raw Input may still see the physical pad.
- Non-XInput PowerA models are unsupported until the HID backend exists.
- XInput offers no serial number, so two identical pads share a VID:PID profile unless told apart by the USB
  path (Phase 5). Wired and Bluetooth may show different IDs.
- ViGEmBus won't get fixes if Windows breaks it.
- **The binaries are unsigned until a certificate is bought**, so SmartScreen will warn.

## 7. Proposed improvements

| Idea | Benefit | Complexity | Risk | Call |
|---|---|---|---|---|
| Emergency bypass hotkey / tray item | Instant recovery | Low | None | **Do** |
| Before/after comparison | Proves it worked | Low | None | **Do** |
| Confidence score | Honest reporting | Low | Could imply false precision; show it as a band | **Do** |
| Drift classification | Different fixes for different faults | Medium | Misclassification; show the numbers | **Done in Core** |
| Calibration history + wear trend | Early warning of failure | Low | Needs weeks of data | Later |
| Latency measurement | Evidence for "no added lag" | Medium | Measures software only | Later |
| Circularity test | Spots a bad gate | Low | None | Phase 5 |
| Per-game profiles | Fits different genres | Medium | Foreground process detection can misfire | Later |
| Compatibility database | Known quirks | High upkeep | Goes stale | Skip |
| Adaptive filtering beyond One Euro | | High | Odd aim feel | Skip |
| Firmware diagnostics | | High | No public API for most pads | Skip |

## 8. Phases and acceptance criteria

| Phase | Done when |
|---|---|
| 1. Investigation | This document |
| 2. Pipeline prototype | `selftest` passes loopback; `run --raw` moves the virtual pad 1:1 with the physical one; Ctrl+C leaves the virtual pad neutral then removed |
| 3. Correction | Unit tests pass; `rest` classifies drift; with a profile, the virtual stick reads about 0 at rest, and full deflection still reaches about 1.0 |
| 4. Hiding | The app still reads the pad when hidden; a game sees one pad; killing the process from Task Manager restores visibility in under 2 s |
| 5. GUI | All pages, keyboard-navigable, no status conveyed by colour alone |
| 6. Reliability | Unplug/replug recovers with no stuck inputs; driver-missing and HidHide-failure paths show clear messages; 1-hour soak with no leaks |
| 7. Packaging | Clean-VM install, uninstall removes hiding config, optional driver removal |

## Sources

- [ViGEm End of Life](https://docs.nefarius.at/projects/ViGEm/End-of-Life/), [ViGEmBus repo](https://github.com/nefarius/ViGEmBus), [releases](https://github.com/nefarius/ViGEmBus/releases), [client on NuGet](https://www.nuget.org/packages/Nefarius.ViGEm.Client)
- [HidHide repo](https://github.com/nefarius/HidHide), [CLI usage](https://docs.nefarius.at/projects/HidHide/CLI-Usage/), [FAQ](https://docs.nefarius.at/projects/HidHide/FAQ/), [API on NuGet](https://www.nuget.org/packages/Nefarius.Drivers.HidHide)
- [VIIPER](https://github.com/Alia5/VIIPER), [usbip-win2](https://github.com/vadimgrn/usbip-win2), [DS4Windows 5 fork](https://github.com/hbashton/DS4Windows)
- [GameInput NuGet docs](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-nuget), [Inno Setup licensing](https://jrsoftware.org/isorder.php)
