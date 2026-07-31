# OpenFlightDisplay Desktop (Windows)

A native Windows client built with .NET 10, C#, WinUI 3 and the Windows App SDK.
Lives at `apps/windows-desktop/`. Architecture and the reasoning behind it:
`docs/adr/0001-windows-desktop-architecture.md`.

> **Status: early Phase 1.** The application builds, launches and shows live
> aircraft from the mock provider on a radar and a flight board. It is not yet a
> replacement for the tablet PWA. What is and is not built is listed below, and
> the in-app navigation says the same thing on each unbuilt page rather than
> showing a blank screen.

## What works today

- Launches as a native Windows app. **No Node.js runtime involved.**
- Mock data source — synthetic moving aircraft, works with no network at all.
- Live radar: range rings with unit-aware labels, cardinal marks, observer
  marker, heading-oriented aircraft symbols, click-to-select.
- Flight board: virtualised list, sortable columns' worth of data per row,
  selection shared with the radar.
- Aircraft detail pane, including a plain-language data-quality summary.
- Explicit status for every feed state — never an unexplained spinner.
- Settings persisted atomically to `%APPDATA%\OpenFlightDisplay\settings.json`
  and read at startup.

## What is not built yet

| Area | State |
|---|---|
| First-run onboarding | Not built. Settings persist, but there is no wizard |
| Settings editor UI | Not built |
| Data-source picker | Not built. **Mock is hardcoded as the active source** |
| Unit switcher in the UI | Not built. Conversion exists and is tested in `Core` |
| Track Flight | Not built (Phase 3). The firmware implements this today |
| History / SQLite | Not built (Phase 2) |
| Alerts and notifications | Not built (Phase 2) |
| Monitoring-area editor | Not built (Phase 2). Domain supports circle/cone/polygon |
| Devices | Not built (Phase 3) |
| Diagnostics | Not built (Phase 4) |
| MSIX packaging / CI | Not built (Phase 4) — proven possible, not wired up |

`adsb.lol` and replay adapters are implemented and tested but cannot be selected
from the UI until the data-source picker lands.

## Building and running

Requires the .NET 10 SDK. **No Visual Studio is needed** — the XAML compiler and
MSIX tooling both resolve through NuGet.

```powershell
dotnet restore apps/windows-desktop/OpenFlightDisplay.Desktop.sln
dotnet build   apps/windows-desktop/OpenFlightDisplay.Desktop.sln -c Release -p:Platform=x64
dotnet test    apps/windows-desktop/OpenFlightDisplay.Desktop.sln -c Release
```

Run the app:

```powershell
dotnet build apps/windows-desktop/src/OpenFlightDisplay.App/OpenFlightDisplay.App.csproj -r win-x64
./apps/windows-desktop/src/OpenFlightDisplay.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/OpenFlightDisplay.App.exe
```

`-p:Platform=x64` is required for solution-level builds: the app targets x64 and
ARM64 only, and the default platform is resolved before the runtime identifier.

## Known defects

### DPI scaling on displays above 100% (open, not fixed)

**Symptom.** On a display with scaling above 100%, the whole UI is rendered
larger than the window. The outer radar rings and the attribution footer fall
outside the client area, and the navigation rail is wider than it should be. On
a 100% display the layout is correct.

**What was measured.** On a 150% display, with a 960x545 physical window, the
radar surface reports `ActualWidth/ActualHeight` of 896x417 — the window's
*physical* size minus chrome, treated as though it were device-independent
pixels. Content drawn at DIP coordinate 448 lands at physical pixel 668, a
factor of 1.4966. The same factor applies to the navigation rail
(48 DIP rendering ~78 physical). So the XAML island lays out using physical
pixels as if they were DIPs and then rasterises at the display scale.

**Ruled out**, each by direct experiment:

- Stale layout state. The scale pass and the aircraft pass once cached different
  sizes; collapsing them into one draw path that reads the host grid at draw
  time fixed their disagreement with each other but not the overall scaling.
- The screenshot method. Reproduces at the window's natural size with no
  programmatic resizing, and via `PrintWindow` as well as screen capture.
- DPI awareness. The process was briefly DPI-unaware after `app.manifest` was
  removed; restoring a minimal manifest brought `GetProcessDpiAwareness` to
  `2` (per-monitor v2) with no change to the symptom.
- Self-contained vs framework-dependent Windows App SDK deployment. No
  difference.

An early measurement suggested the display was at 100%; that reading came from
`GetDeviceCaps` in a DPI-unaware PowerShell process, which always reports 96 DPI,
and was wrong.

**Next steps.** Most likely in the unpackaged WinUI bootstrap path — the window
is created and sized before the island learns the monitor's scale. Worth trying:
running packaged (MSIX) to see whether it reproduces; handling `WM_DPICHANGED`
explicitly; and setting the window size through `AppWindow` rather than relying
on the default.

### Flight board rebuilds every row on each poll

`FlightBoardViewModel.SyncRows` discards and recreates all rows per poll. Fine at
a dozen aircraft, not at the 1,000-aircraft target. Needs a keyed diff against
`IcaoHex`. To be measured via the diagnostics page rather than guessed at.

## Toolchain notes

Two traps cost real time and are commented at the point where they bite:

- **Do not reference `Microsoft.Windows.CsWinRT` explicitly.** `Microsoft.WindowsAppSDK`
  brings the matching version transitively. A direct reference activates a
  projection step that requires the Windows SDK to be registered in the
  registry, which fails on a machine with only the .NET SDK installed.
- **Keep `app.manifest` minimal.** It declares DPI awareness and nothing else.
  Adding `assemblyIdentity` or a `supportedOS` block replaces the manifest the
  Windows App SDK generates for an unpackaged app — which carries the WinRT
  activatable-class registrations — and the app then builds fine but fails at
  startup with *"the application has failed to start because its side-by-side
  configuration is incorrect"*.

Symbol (`.appxsym`) packages cannot be produced on a machine without the Visual
Studio C++ toolset, because `mspdbcmf.exe` is absent. CI on `windows-latest` has
it.

## Privacy

Unchanged from the rest of the project: local-first, no account, no telemetry.
History is off by default. Provider credentials belong in Windows Credential
Manager, never in `settings.json`. No real location is committed to the
repository — the fallback coordinate in `MainWindow` is a well-known public one.
