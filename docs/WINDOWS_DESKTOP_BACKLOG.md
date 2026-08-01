# OpenFlightDisplay Desktop — backlog and open problems

Working state of `apps/windows-desktop` as of **2026-08-01**, branch
`feat/windows-dotnet10`.

Written to be picked up cold. Everything below is either measured or explicitly
marked as unverified — nothing here is aspirational.

## Where it stands

| | |
|---|---|
| Tests | **287 passing**, 0 warnings under warnings-as-errors |
| Build | Clean `dotnet build -c Release -p:Platform=x64` |
| Packaging | MSIX verified locally: 88.8 MB x64, 86.3 MB ARM64 |
| App | Launches, polls, renders. No Node.js involved |

Baseline of the rest of the monorepo is unchanged: 217/217 original tests still
pass (104 firmware native + 113 TypeScript).

### What works end to end

Onboarding → data source (mock / adsb.lol live / local receiver / replay) →
radar with trails → flight board with detail pane → history to SQLite → alerts
with Windows toasts → CSV/JSON/GeoJSON export.

Verified against real data, not only fixtures: adsb.lol returned 106 live
aircraft; the history database was inspected after a run and had 228
observations across 12 mock aircraft with nulls preserved and **zero**
`altitude_ft = 0` rows.

---

## Open defects

### 1. DPI scaling above 100% — OPEN, highest impact

The whole UI renders larger than the window on a scaled display. Outer radar
rings and the attribution footer fall outside the client area. **A 100% display
is unaffected**, which is why it was not obvious immediately.

Measured on a 150% display with a 960×545 physical window: the radar surface
reports `ActualWidth/Height` of 896×417 — the window's *physical* size minus
chrome, treated as though it were device-independent pixels. Content drawn at
DIP 448 lands at physical pixel 668, a factor of **1.4966**. The navigation rail
is oversized by the same factor, so it is the whole visual tree, not one control.

Ruled out by experiment, each recorded in `WINDOWS_DESKTOP.md`:

- Stale layout state (collapsed the two draw passes into one — no change)
- The screenshot method (reproduces at natural size, and via `PrintWindow`)
- DPI awareness (`GetProcessDpiAwareness` now returns `2`, per-monitor v2 — no change)
- Self-contained vs framework-dependent Windows App SDK deployment — no change

**Untried leads, in the order worth trying:**

1. Run the **packaged MSIX** rather than unpackaged. Packaging changes how the
   Windows App SDK bootstraps, and this has never been tested — installing needs
   a trusted certificate, procedure in `WINDOWS_DESKTOP.md`.
2. Handle `WM_DPICHANGED` explicitly.
3. Size the window through `AppWindow` rather than relying on the default.

> One earlier measurement claimed the display was at 100%. That reading came
> from `GetDeviceCaps` inside a DPI-unaware PowerShell process, which always
> reports 96 DPI. It was wrong and it wasted time — measure DPI from inside a
> DPI-aware process.

### 2. CI has never executed — OPEN

`.github/workflows/windows-desktop.yml` and `windows-desktop-release.yml` are
valid YAML and every `dotnet` command in them was proven locally, but **no
GitHub Actions run has ever executed them**. The pinned action versions
(`checkout@v7`, `setup-dotnet@v5`, `upload-artifact@v7`) were matched to the
repo's existing convention and have not been resolved against the registry.
Expect the first run to need adjustment.

### 3. No UI automation tests — OPEN

There is no test project for `OpenFlightDisplay.App`. View models and controls
are exercised only by running the app. The domain, providers, persistence and
infrastructure are well covered; the WinUI layer is not covered at all.

---

## Backlog, roughly in value order

### Phase 3 — finish flight tracking

The **domain is ported and tested** (62 tests, thresholds matching the firmware
exactly). **Nothing in the app calls it.** Needed:

- A Track Flight page: enter callsign / flight number, destination ICAO, travel
  minutes, walk-out minutes
- A callsign-lookup poller using `adsb.lol /v2/callsign/{callsign}` on the
  adaptive cadence `FlightTracking.PollIntervalFor` already computes
- An airport lookup resolving `KSEA` to coordinates **and field elevation** —
  elevation is load-bearing, landing is judged against it
- Wire `DepartureAdvice` into a toast so "leave now" actually reaches the user

Reference: `docs/DISPLAY_UI.md` for how the firmware presents this.

### Phase 3 — remaining

- Gateway client mode (`DataMode.Gateway`) — listed in the picker, disabled,
  says which phase it is planned for
- Core2 device discovery, pairing, configuration, status
- Replay recording **file loader** — `ReplayProvider` works and is tested, but
  no UI opens a recording, so selecting Replay reports "complete" immediately
- Provider credentials in Windows Credential Manager (nothing needs a key today;
  `AppSettings` deliberately holds no secrets)

### Phase 2 — remaining UI

The engines exist and are tested; these are the missing surfaces.

- **Alert rule editor.** One built-in rule ships (emergency squawk, 15 min
  cooldown, exempt from quiet hours). `AlertEvaluator` supports area entry/exit,
  approach-within, descent-below, quiet hours and per-poll caps — none reachable
- **Monitoring-area editor.** `Core` supports circle, cone and polygon with
  altitude bands. Only a circle from settings is ever constructed
- **Filter builder** and a **ranking-mode picker.** Seven ranking modes exist;
  the UI always uses `NearestHorizontal`
- **History browsing / timeline playback.** Trails render; nothing else reads
  the database
- **Export UI for trails** — `AircraftExporter.TrailToGeoJson` has no caller
- Compact always-on-top window, system tray, background monitoring

### Phase 4 — hardening

- Accessibility review (keyboard nav, screen reader, high contrast, reduced
  motion). Rows carry `AccessibleDescription`; nothing has been audited
- Sleep/resume and network-failure testing
- Multi-hour soak. Memory is flat over ~45 s at 1,000 aircraft; longer is unknown
- Screenshots and a troubleshooting guide
- Symbol packages need `mspdbcmf.exe` from the VS C++ toolset — absent locally,
  present on `windows-latest`

---

## Things not to undo

Hard-won, each cost real debugging time. All are commented at the point they
matter, but they are easy to "tidy" away:

1. **Do not reference `Microsoft.Windows.CsWinRT` explicitly.** The Windows App
   SDK brings the matching version transitively. A direct reference activates a
   projection step needing the Windows SDK in the registry, which fails without
   Visual Studio.
2. **Keep `app.manifest` minimal** — DPI awareness only. Adding
   `assemblyIdentity` or `supportedOS` replaces the manifest the SDK generates
   and the app builds fine, then fails at startup with *"side-by-side
   configuration is incorrect"*.
3. **`Package.appxmanifest` must stay conditional** on `WindowsPackageType != None`.
   Unconditional inclusion breaks `dotnet build` outright.
4. **The radar redraw must stay coalesced.** Redrawing per `CollectionChanged`
   event was a thousand full redraws per poll — 112% of a core, +285 MB/25 s.
   Coalescing took it to 18% and flat memory.
5. **Startup must run from `Loaded`, not the constructor.** `ContentDialog`
   needs a live `XamlRoot`; running it earlier threw into a discarded task and
   the app sat on "Setup required" forever with no error.
6. **`SQLitePCLRaw` pins are deliberate.** `Microsoft.Data.Sqlite 10.0.10`
   resolves `lib.e_sqlite3 2.1.11`, which carries a HIGH severity advisory
   (GHSA-2m69-gcr7-jv3q). Remove the pins only once upstream resolves a clean
   version by itself.
7. **The airline-table parity test reads the firmware's C++ directly.** It is
   what stops the duplicated IATA→ICAO table drifting. If `airline.cpp` moves,
   update the test rather than deleting it.
8. **Nullable means "not reported", never zero.** Enforced from the model
   through the database to CSV and GeoJSON, with tests in both directions. It is
   the single most load-bearing rule in this codebase.

---

## Useful commands

```powershell
# Build and test
dotnet build apps/windows-desktop/OpenFlightDisplay.Desktop.sln -c Release -p:Platform=x64
dotnet test  apps/windows-desktop/OpenFlightDisplay.Desktop.sln -c Release -p:Platform=x64

# Run (unpackaged)
dotnet build apps/windows-desktop/src/OpenFlightDisplay.App/OpenFlightDisplay.App.csproj -r win-x64
./apps/windows-desktop/src/OpenFlightDisplay.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/OpenFlightDisplay.App.exe

# Package
dotnet publish apps/windows-desktop/src/OpenFlightDisplay.App/OpenFlightDisplay.App.csproj `
  -c Release -r win-x64 -p:Platform=x64 `
  -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

Settings and history live in `%APPDATA%\OpenFlightDisplay\`. Deleting
`settings.json` re-triggers onboarding. The app is single-instance, so a stale
process will silently absorb a new launch — check Task Manager if a rebuild
seems not to take effect.

To load-test, construct `MockProvider` with a larger count in
`App.xaml.cs` (`new MockProvider(1000)`).
