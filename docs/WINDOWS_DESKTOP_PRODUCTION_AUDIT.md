# Windows desktop — production readiness audit

Measured on 2026-08-01 against commit `64fb20b`, on Windows 11 26200, x64,
1920×1200 at 150%. Every number here was taken from a running Release build or
from the toolchain, not estimated.

## Verdict

The application is **functionally strong and operationally healthy**, and is
**not yet ready to hand to strangers**. Nothing found is a design flaw; the gaps
are the ones a project accumulates when it has been built quickly and never
shipped.

Three things block a release, and none of them are performance.

---

## What is already good

| | Measured |
|---|---|
| Cold start to visible window | **0.70 s** |
| Working set, steady state | **184 → 185 MB** over 60 s — flat |
| Private bytes | 103–104 MB — flat |
| Handles | 2151 → 2143 — no leak |
| Threads | 74 → 70 — stable |
| CPU, live feed + map | ~4% of one core |
| Vulnerable dependencies | **none**, including transitive |
| Compiler/analyzer warnings | **0**, under warnings-as-errors |
| TODO / FIXME / HACK markers | **0** |
| Tests | 436 passing |

Memory and handles being flat over a sustained run is the single most reassuring
number here — it is the failure mode that would otherwise only appear after a
user left it running overnight.

---

## Blocking issues

### B1. `async void` handlers — PARTLY FIXED 2026-08-01

**This has already taken the process down once.** An exception escaping an
`async void` method on the UI thread kills the application outright: no dialog,
no message, and nothing in the event log but a stowed-exception code. The map
tile race on 2026-08-01 presented to the user as *"it froze on launch"*.

| Location | Count |
|---|---|
| `MainWindow.xaml.cs` | 22 |
| `OnboardingDialog.xaml.cs` | 2 |
| `RadarView.xaml.cs` | 1 (now guarded) |

Several await operations that can genuinely throw. The clearest is
`ContentDialog.ShowAsync`, which throws if another dialog is already open —
reachable today by double-clicking **Add rule**.

`App.UnhandledException` now writes `%APPDATA%\OpenFlightDisplay\crash.log`, so
the next one will at least be diagnosable. That is a net, not a fix.

**Fixed for the twelve handlers that can genuinely throw**, via `SafeHandler`
and a `Safe(...)` wrapper in `MainWindow`, reporting to a new application-level
`InfoBar` rather than to a page-specific label.

Verified against the real defect: double-clicking **Add rule** used to kill the
process. It now survives and shows *"Something went wrong: An async operation
was not properly started. Only a single ContentDialog can be open at any time."*

**Remaining: 10 `async void` in `MainWindow`, 2 in `OnboardingDialog`.** Those
await work that either cannot throw or already has its own `try` — but that is a
judgement about today's code, not a guarantee. They should go through `Safe`
too as `MainWindow` is split up (B2).

### B2. `MainWindow.xaml.cs` was 2,133 lines — FIXED 2026-08-02

It owns nine pages, the feed, history, alerts, tracking, recording, the map, the
place search, compact mode and settings. It is the only file in the project that
is genuinely hard to work in, and every feature this session made it worse.

| File | Lines |
|---|---|
| `MainWindow.xaml.cs` | **2,133** |
| `MainWindow.xaml` | 900 |
| `RadarView.xaml.cs` | 738 |
| Everything else | < 480 |

The rest of the codebase averages under 220 lines a file. This is an outlier, not
a house style.

**Fixed.** Split at the existing `// ---- x ----` markers into eight feature
partials. No behaviour change; it compiled clean first time and all 436 tests
pass.

| File | Lines |
|---|---|
| `MainWindow.xaml.cs` | 928 (was 2,133) |
| `MainWindow.Tracking.cs` | 358 |
| `MainWindow.Areas.cs` | 245 |
| `MainWindow.History.cs` | 219 |
| `MainWindow.Recording.cs` | 174 |
| `MainWindow.Diagnostics.cs` | 150 |
| `MainWindow.Compact.cs` | 181 |
| `MainWindow.Places.cs` | 124 |
| `MainWindow.Map.cs` | 90 |

`MainWindow.xaml.cs` is still the largest file in the project and could lose the
settings form next, but it is no longer an outlier by an order of magnitude.

### B2a. 31 mojibake sequences, 5 of them user-visible — FIXED 2026-08-02

Found while verifying the split preserved bytes. **Pre-existing**, not introduced:
29 were present in `HEAD` before the refactor.

Em dashes had been decoded as CP1252 and re-encoded as UTF-8, leaving the literal
three characters `â€”` (`U+00E2 U+20AC U+201D`) where `—` (`U+2014`) belonged.
Verified at the byte level — `C3 A2 E2 82 AC E2 80 9D` — so it was genuine
double encoding, not a terminal display artifact.

**Five were inside string literals and reached the screen**, including the
Track Flight page's "no distance" placeholder, which rendered as `â€”` instead of
`—`, and `"Searchingâ€¦"` on the place search.

All 31 repaired and confirmed in the running UI, which now reports `U+2014`.
Worth noting the XAML files were clean; only the C# was affected.

### B3. The WinUI layer has zero tests — HIGH

| Project | Source files | Test files |
|---|---|---|
| Core | 18 | 11 |
| Providers | 12 | 6 |
| Infrastructure | 5 | 3 |
| Persistence | 2 | 1 |
| **App** | **15** | **0** |

Every bug that reached the user this session lived in the App layer: the
`ComboBoxItem.IsSelected` startup crash, the map tile race, the clipped compact
panel. The layers with tests produced none.

Not all of it is testable without a UI harness, but the view models and the
zoom-ladder logic are plain classes and are not tested at all.

**Fix:** an `OpenFlightDisplay.App.Tests` project covering view models, then
extract testable logic out of the code-behind as B2 proceeds.

---

## Optimisations worth doing

### O1. ~38 MB of unused ML runtime is shipped — MEDIUM

`Microsoft.WindowsAppSDK` 2.3.1 is a metapackage. It pulls in:

- `Microsoft.WindowsAppSDK.AI/2.3.4`
- `Microsoft.WindowsAppSDK.ML/2.1.74`
- `Microsoft.Windows.AI.MachineLearning/2.1.74`

which deliver `onnxruntime.dll` (20.7 MB) and `DirectML.dll` (17.8 MB). **The
application uses no AI or ML APIs.** That is ~17% of the deployed payload.

Excluding them is plausible but **must be verified by running**, not by building
— the Windows App SDK resolves parts of itself at startup, and this project
already has a scar from a related change (the CsWinRT non-reference, item 1 in
"things not to undo"). Treat as an experiment with a revert path.

### O2. A stale nested `publish` folder inflates the build output — LOW

The Release output folder is 450 MB, of which **224.6 MB is a nested `publish`
subdirectory** left by an earlier `dotnet publish`. The actual runnable payload
is ~225 MB. This is build detritus, not shipped, but it makes the deployment
size look twice what it is and should be cleaned or redirected.

### O3. 17 awaits in library code without `ConfigureAwait` — LOW

In `Core`, `Providers`, `Infrastructure`, `Persistence`. There is no
`SynchronizationContext` in these paths today so it is currently harmless, but
library code capturing context is how a deadlock arrives later. Mechanical.

---

## Release engineering gaps

These are not code problems and each one is a genuine blocker for a public
release:

| Gap | State |
|---|---|
| **CI has never run** | Workflows valid, all six pinned action versions verified against the GitHub API, but no run has executed. 17 commits sit unpushed |
| **Code signing** | MSIX is built with `AppxPackageSigningEnabled=false`. An unsigned package cannot be installed without the user trusting a certificate by hand |
| **Version number** | Fixed at `0.1.0.0`. No versioning scheme, no changelog |
| **Symbol packages** | `.appxsym` generation needs `mspdbcmf.exe` from the VS C++ toolset, absent locally, present on `windows-latest`. No crash will be symbolicated until CI produces them |
| **ARM64** | Builds, never executed. No ARM64 machine here |
| **Update mechanism** | None. No way to tell a user a new version exists |
| **Licence** | Repository is MIT; the desktop app displays no licence or third-party notices |

---

## Correctness items still open

| Item | Severity | Note |
|---|---|---|
| `AppSettings.AlertRules` reference equality | Low | Whole-settings equality is unreliable once rules exist. `MonitoringAreaSetting` was fixed; this was not. Nothing compares whole settings today |
| Single instance does not span builds | Low | Debug and Release copies run simultaneously and share `history.db`. Developer-only |
| Mixed-DPI multi-monitor | Unknown | `WM_DPICHANGED` never exercised. Single-monitor 150% verified |
| Multi-hour soak | Unknown | Flat over 60 s and over ~45 s at 1,000 aircraft. Overnight untested |
| Sleep/resume | Unknown | Never tested |

---

## Recommended order

1. **B1** — the async void safety net. Highest risk reduction per hour, and it
   stops the failure mode the user has already seen twice.
2. **B2** — split `MainWindow`. Unblocks B3 and makes everything after it easier.
3. **B3** — an App test project, starting with view models.
4. **CI** — push and let it run. Everything below depends on knowing the build is
   reproducible off this machine.
5. **O1** — try the 38 MB trim, with a revert path.
6. Signing, versioning, licence notices.

O2 and O3 are ten-minute jobs and can be done whenever.
