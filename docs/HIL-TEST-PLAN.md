# Hardware test plan

Automated tests use fakes, so they prove the logic but not your hardware. Run these on the PC with the
controller, in order, and stop at the first failure. The first failure tells us the cause.

Setup: install [ViGEmBus 1.22.0](https://github.com/nefarius/ViGEmBus/releases) (a kernel driver, your decision,
no restart expected). **Leave HidHide cloaking off for all of Part A:** open the HidHide Configuration Client and
untick "Enable device hiding", then close the client.

## Part A: the pipeline (Phase 2)

| # | Command | Pass |
|---|---|---|
| A1 | `stickfix devices` with the pad plugged in | Exactly one slot listed, with an ID such as `20D6-xxxx`. **If nothing is listed, your PowerA isn't an XInput pad; tell me and I'll prioritise the HID backend** |
| A2 | `stickfix selftest` | `PASS: all ... states read back identical` |
| A3 | `stickfix raw`, with another window focused | Values change as you move the sticks. Every button and trigger changes the display |
| A4 | `stickfix run --raw` | `RAW`, `EMIT` and `VIRT` agree as you move everything, and `mismatch` stays near 0 |
| A5 | While A4 runs, open `joy.cpl` and select the virtual pad | Its stick and buttons move |
| A6 | Hold A and full left stick, press Ctrl+C | Virtual pad disappears from `joy.cpl`, nothing stuck |
| A7 | During `run --raw`, hold A and unplug | Status shows `DISCONNECTED`, `EMIT` goes neutral |
| A8 | Replug | Forwarding resumes with no restart |
| A9 | During `run --raw`, kill `stickfix.exe` in Task Manager | Virtual pad disappears from `joy.cpl`. This confirms the crash assumption in the design |

Send me the A1 and A2 output, plus any row that failed.

## Part B: correction (Phase 3)

| # | Command | Pass |
|---|---|---|
| B1 | `stickfix rest`, not touching anything, 3 times | Medians agree within about 0.02. Note left-stick Y, and whether it matches your -0.21 |
| B2 | `stickfix rest`, bumping a stick on purpose | Reports `MovedDuringTest` |
| B3 | `stickfix calibrate`, answer `y` | Reports a small deadzone and saves a profile, or explains why it refuses |
| B4 | `stickfix run`, hands off | `RAW` shows the offset, `EMIT` shows exactly 0 |
| B5 | Move each stick fully in 8 directions | `EMIT` reaches about ±1.0 in all four main directions |
| B6 | Move very gently | `EMIT` starts moving soon after `RAW` leaves its rest value |
| B7 | Press Space | `BYPASS` shows; `EMIT` equals `RAW` |

## Part C: hiding (Phase 4, not built yet)

Only after Parts A and B pass. The top question: does `xinput1_4` in an allow-listed app still read a pad hidden
on both its `HID\` and `USB\` nodes? Everything in Phase 4 depends on that answer.
