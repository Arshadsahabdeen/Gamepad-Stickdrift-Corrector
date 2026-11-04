# Testing

`dotnet test` proves the logic using simulated devices. Hardware behaviour needs the checks below, which were last run
on one PowerA `20D6:2005` over USB on Windows 11.

To check what a game would see, use a process started **after** hiding: one that opened the pad earlier keeps its
access, which gives a false alarm.

## Core

| # | Do this | Pass |
|---|---|---|
| 1 | Calibrate, rotating the sticks fully | About 0% off center after, about 100% reach, saves |
| 2 | Calibrate without moving the sticks | Refuses to save unless overridden |
| 3 | Hands off, watch Home | Solid dot at center, `Games` reads 0.000 |
| 4 | Push each stick in four directions | Solid dot reaches the edge |
| 5 | Press every button and trigger | Each one registers |
| 6 | Untick **Fix drift** | `Games` matches `Raw`. Tick it again and it returns to 0 |

## Hiding

| # | Do this | Pass |
|---|---|---|
| 7 | Tick **Hide the real controller from games**, then open `joy.cpl` | One controller listed |
| 8 | Start a game | One player, no drift |
| 9 | End `StickFix.exe` in Task Manager | Real controller back within seconds |
| 10 | Hide, then unplug and replug | Recovers, still hidden, nothing stuck |
| 11 | Help, **Restore my controller** | Real controller back, StickFix stopped |

## Tray

| # | Do this | Pass |
|---|---|---|
| 12 | Minimize | Gone from taskbar, icon in tray, correction keeps running |
| 13 | Double-click the tray icon, or open StickFix again | Same window returns, no second copy |
| 14 | Tray menu, **Fix drift** and **Exit** | Toggles correction. Exit restores the controller |
| 15 | Tick **Start with Windows**, then sign in again | Starts in the tray with no window |

## Installer

Driver steps need a PC without the drivers, such as a clean VM.

| # | Do this | Pass |
|---|---|---|
| 16 | Run setup on a clean PC with both boxes ticked | Both driver setups open and complete. Asks to restart if needed |
| 17 | Run setup where the drivers exist | Boxes disabled, marked already installed |
| 18 | Untick both boxes on a clean PC | App installs. Home offers the driver links |
| 19 | Uninstall while hidden | Controller restored, offered to delete settings, drivers remain |
