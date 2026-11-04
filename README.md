# StickFix

Windows controller drift diagnosis, calibration and correction through a virtual Xbox pad.

**Status: command-line prototype (Phases 1 to 3). Not yet proven on real hardware. There is no GUI, hiding or
installer yet.**

- [Architecture review and roadmap](docs/ARCHITECTURE.md)
- [Hardware test plan](docs/HIL-TEST-PLAN.md)

## Build and test

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test
```

```bash
dotnet publish src/StickFix.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`publish/stickfix.exe` then runs on any 64-bit Windows 10/11 PC with no .NET installed. `stickfix` with no
arguments lists the commands. Forwarding needs the ViGEmBus driver.

## Layout

| Path | Contents |
|---|---|
| `src/StickFix.Core` | Correction maths, diagnosis, profiles, forwarding engine. No OS calls |
| `src/StickFix.Windows` | XInput reader, ViGEm virtual pad |
| `src/StickFix.Cli` | Prototype commands |
| `tests/StickFix.Tests` | Unit tests using fake devices |
