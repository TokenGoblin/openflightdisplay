# OpenFlightDisplay Desktop — backlog and open problems

Working state of `apps/windows-desktop` as of **2026-08-01**, branch
`feat/windows-dotnet10`.

Written to be picked up cold. Everything below is either measured or explicitly
marked as unverified — nothing here is aspirational.

## Where it stands

| | |
|---|---|
| Tests | **423 passing**, 0 warnings under warnings-as-errors |
| Build | Clean `dotnet build -c Release -p:Platform=x64` |
| Packaging | MSIX verified locally: 88.8 MB x64, 86.3 MB ARM64 |
| App | Launches, polls, renders. No Node.js involved |

Baseline of the rest of the monorepo is unchanged: 217/217 original tests still
pass (104 firmware native + 113 TypeScript).

### What works end to end

Onboarding → data source (mock / adsb.lol live / local receiver / replay) →
radar with trails → flight board with detail pane → history to SQLite → alerts
with Windows toasts → CSV/JSON/GeoJSON export → **follow a flight to its
destination with ETA and departure advice**.

Verified against real data, not only fixtures: adsb.lol returned 106 live
aircraft; the history database was inspected after a run and had 228
observations across 12 mock aircraft with nulls preserved and **zero**
`altitude_ft = 0` rows.

---

## Open defects

### 1. DPI scaling above 100% — CLOSED, was never a defect

The app renders correctly at 150%. Instrumented from inside the process:
`XamlRoot.Size` 945.3×507.3 DIP × `RasterizationScale` 1.5000 = 1418 physical,
which is `GetClientRect` exactly. A capture from a DPI-aware thread shows all
four rings, all four cardinals and the footer inside the window.

The "960×545 physical window" the whole investigation rested on was
`GetWindowRect` called from DPI-*unaware* PowerShell, which sees a virtualised
desktop and returned the real 1440×817 divided by 1.5. Capturing that rect
against the unvirtualised screen crops the top-left two-thirds of the window —
cutting off precisely the outer rings and the footer, and making what remains
look 1.5× oversized. "Content at DIP 448 lands at physical 668" was correct
rendering being read as the bug.

Full write-up in `WINDOWS_DESKTOP.md`. **Capture windows only with
`apps/windows-desktop/tools/Capture-Window.ps1`**, which sets per-monitor DPI
awareness on the calling thread first. Two wrong readings here have now come
from measuring a window from a DPI-unaware process.

Genuinely untested, and the honest remainder of this item: monitors with
*different* scale factors (`WM_DPICHANGED`). Only single-monitor 150% is
verified.

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

### Phase 3 — flight tracking — DONE

Built and verified against live data on 2026-08-01. Tracking `RYR89ZN` to
`EGLL` reported ENROUTE, 14 min ETA, 107 NM, field elevation 83 ft, and
"RUNNING LATE — about 11 minutes past the ideal departure time"
(14 + 20 − 45 = −11, past the −10 threshold).

- `AirportLookup` → `/api/0/airport/{icao}`, returning coordinates and
  `alt_feet`. Confirmed live: **the endpoint answers HTTP 200 with a literal
  `null`** for unknown codes *and* for any IATA code, so "parsed" is not "found"
- `AdsbLolProvider.FetchByCallsignAsync` → `/v2/callsign/{callsign}`. Confirmed
  live: a callsign with no active flight is HTTP 200 with `ac: []`, not a 404,
  so "not flying" is an empty success rather than a failure
- `FlightTrackingService` (Infrastructure) drives the adaptive cadence
- Track Flight page, with the tracked flight persisted and resumed on launch
- `DepartureAdvice` raises a toast on **change**, at LeaveSoon and LeaveNow only

Still untested: the toast itself has not been observed firing, because
notifications are opt-in and were off. The code path is the same
`ToastAlertNotifier` the emergency-squawk alert already uses.

Reference: `docs/DISPLAY_UI.md` for how the firmware presents this.

### Phase 3 — remaining

- Gateway client mode (`DataMode.Gateway`) — listed in the picker, disabled,
  says which phase it is planned for
- Core2 device discovery, pairing, configuration, status
- ~~Replay recording **file loader**~~ **DONE.** Record a live session and play
  it back. Verified on 2026-08-01: recorded 3 frames of live adsb.lol traffic
  (23 aircraft each), loaded from disk, played through and reported "Replay
  complete — reached the end of adsblol-20260801-183441".
  - Format is **JSON Lines** (`.ofdreplay`): a header line then one frame per
    line. A single JSON document would be unparseable if a session died before
    the closing bracket; JSON Lines loses at most the final partial line, and the
    loader skips damaged lines, counts them and says so
  - Frames carry full `AircraftState`, so nullable-means-not-reported survives.
    Computed properties are excluded from the file so they cannot drift from the
    fields they derive from
  - The loaded recording lives on `ProviderRegistry`, not in settings: a path
    saved across launches would silently become an empty replay once the file
    moved
- Provider credentials in Windows Credential Manager (nothing needs a key today;
  `AppSettings` deliberately holds no secrets)

### Phase 2 — remaining UI

The engines exist and are tested; these are the missing surfaces.

- ~~**Alert rule editor.**~~ **DONE.** All five triggers are reachable, with
  thresholds, per-rule cooldown, quiet hours and the toast channel. Verified
  against live traffic on 2026-08-01: an "approaches within 15 km" rule fired on
  ASA585, ASA697, EJA655, KEN2020 and AIRLIFT — exactly five, which is
  `MaxEventsPerPoll` capping the poll as designed. Rules persist to settings and
  the editor refuses to save one that could never fire.
  - Rules bind to **the configured monitoring area** rather than carrying their
    own geometry. That is what makes area triggers usable before the area editor
    exists, and it avoids serializing a polymorphic `MonitoringArea`
  - `AlertChannels.Sound` is deliberately **not** offered: nothing plays a sound,
    so a switch for it would be a lie
- ~~**Monitoring-area editor.**~~ **DONE** (form-based, not map-based). Circle,
  cone and polygon with altitude bands, all persisted. Verified live: switching
  to a 60°-wide cone facing 090° narrowed the board as expected and round-tripped
  through settings.
  - A centre-relative area stores **no coordinates** and resolves against the home
    location, so moving home moves the area instead of leaving it over the old
    address
  - An area that cannot be built is refused on save rather than saved and
    silently matching nothing
  - Still worth doing: a **map-based** editor. Typing polygon vertices works and
    reports the offending line number, but it is not pleasant. The radar's
    OpenStreetMap backdrop now provides the tile plumbing this would build on

### Map backdrop — DONE

The radar draws optional OpenStreetMap tiles beneath its rings. Native raster
tiles, **not** the WebView2 + MapLibre route ADR-0001 §6 chose — that decision
was for an *interactive* map surface, and a fixed north-up backdrop at one zoom
needs no browser, no JS bridge and no API key. The ADR's decision still stands
for a future interactive map page.

- The radar remains the source of truth for scale; tiles are scaled to its
  pixels-per-kilometre. A test asserts the observer's map pixel lands exactly at
  the plot centre — a backdrop offset from the symbols looks authoritative and is
  worse than none
- **Off by default and disclosed.** It is the only feature that sends the user's
  location anywhere but their chosen aviation-data provider
- OSM tile policy compliance is itemised in `docs/ATTRIBUTION.md`. Do not raise
  `SlippyMap.MaxZoom`, remove the per-draw tile cap, or add subdomain rotation —
  each exists to honour that policy
- ~~**Filter builder** and a **ranking-mode picker.**~~ **DONE.** All seven
  ranking modes are selectable. `AircraftFilter` is new in `Core` — altitude
  band, airborne only, callsign required, emergencies only — applied before
  ranking and before recording. Verified live: a 20,000 ft floor took the board
  from 17 aircraft to 2.
  - **An unreported measurement never fails a filter.** An aircraft with no
    altitude is not at zero feet, so an altitude filter cannot honestly exclude
    it. Unknown passes
  - An empty board caused by a filter says so and names the filter, rather than
    reusing "No aircraft in range" — otherwise a user concludes the feed broke
- **History browsing / timeline playback.** Trails render; nothing else reads
  the database
- **Export UI for trails** — `AircraftExporter.TrailToGeoJson` has no caller
- Compact always-on-top window, system tray, background monitoring

### Phase 4 — hardening

- Accessibility: **partly audited on 2026-08-01**, by walking the live UI
  Automation tree across all nine built pages.
  - **Names: done.** 50 on-screen interactive controls, **0 unnamed**. Three real
    defects were found and fixed: a `ComboBox` labelled only by a neighbouring
    `TextBlock` exposes no name (a visible heading is not a label), a `CheckBox`
    whose content is a panel rather than a string exposes none either, and a
    `ToggleSwitch` with empty on/off content exposes none. Repeated per-row
    buttons now name their row — a column of identical "Edit", "Delete" and
    "Export trail" buttons is useless to a screen reader
  - Radar symbols were already good: 23 exposed full descriptions such as
    *"N820KE, 3.2 NM away, bearing 112° ESE, altitude 1,425 ft, CLIMB"*
  - **Keyboard: 0 of 50 controls were unreachable**, so tab order exists, but the
    *order* itself has not been checked
  - **Not yet done:** high contrast, reduced motion, and a real screen-reader
    pass with Narrator. An automation-tree walk proves names exist; it does not
    prove the result is pleasant to listen to
- Sleep/resume and network-failure testing
- Mixed-DPI multi-monitor: drag the window between monitors at different scale
  factors and confirm `WM_DPICHANGED` is handled. Single-monitor 150% is verified;
  this is not
- Multi-hour soak. Memory is flat over ~45 s at 1,000 aircraft; longer is unknown
- ~~Screenshots and a troubleshooting guide~~ **DONE** —
  `docs/WINDOWS_DESKTOP_TROUBLESHOOTING.md`, with five real screenshots. Every
  symptom in it actually happened during development
- Cosmetic: the outermost ring label sits on top of the `N` cardinal mark — both
  are drawn near `CentreX` at the top of the plot. Visible in every radar
  capture. Not a scaling problem; the two labels just need offsetting
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
9. **The airport lookup must keep "not found" separate from "lookup failed".**
   The endpoint returns HTTP 200 with a literal `null` for a code it does not
   know, so a successful parse is not a successful lookup. Collapsing the two
   tells somebody their correct ICAO code is wrong when the network is down —
   and defaulting a missing `alt_feet` to zero would judge Denver's arrivals
   against sea level and never report a landing.
10. **The callsign match is confirmed, not assumed.** `/v2/callsign/` matches on
    callsign, but the row is checked before use. A mismatch would put a
    stranger's aircraft on a screen someone uses to decide when to leave.
11. **Departure advice fires on change, never per poll.** A ten-second toast
    cadence through an approach trains the user to dismiss the one that matters.
12. **`AppSettings.AlertRules` is nullable and null is not empty.** Null means
    "never configured" and seeds the emergency rule; an empty list means the user
    deleted every rule and must be left alone. Collapsing them either leaves the
    alert engine dormant or overrides a deliberate choice.
13. **Dialog `Result` properties are `internal`, not `public`.** XAML's type-info
    generator emits an activation stub for the type of every public property on a
    XAML-backed class. A public `Result` of a type with `required` members fails
    the build with CS9035 in generated code — a confusing error a long way from
    its cause.
14. **Alert rules are evaluated even when notifications are off.** The master
    switch governs toasts, not whether alerts happen; installing no rules when it
    is off left the in-app alert list permanently and silently empty.
15. **`MonitoringAreaSetting` has a hand-written `Equals`.** A record compares an
    `IReadOnlyList<T>` member by *reference*, so an area was never equal to itself
    after a save and reload — writing produced an array, reading produced a list.
    Add new properties to that method as well as to the record.
    **`AppSettings.AlertRules` has the same latent problem** and is not yet
    fixed: whole-`AppSettings` equality is unreliable once rules are configured.
    Nothing in the app compares whole settings objects today, but do not start
    without fixing it.
16. **A `ComboBoxItem` must not carry `IsSelected="True"` in markup** when its
    `ComboBox` has a `SelectionChanged` handler. The event fires during
    `InitializeComponent`, before the rest of the page exists, and the app dies
    at startup with a stowed `E_POINTER` and no usable exit code. Choose the
    default in the constructor with events suppressed.

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
