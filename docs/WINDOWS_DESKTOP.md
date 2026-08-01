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
| MSIX packaging / CI | **Built.** See "Packaging" below |

### Local ADS-B receivers

Reading directly from your own dump1090, readsb or tar1090 install is supported.
Select **Local ADS-B receiver** as the data source and set the receiver URL in
Settings. A bare host is enough — `http://192.168.1.10` or
`http://raspberrypi.local` — because the app probes the common paths
(`/data/aircraft.json`, `/dump1090/data/aircraft.json`,
`/dump1090-fa/data/aircraft.json`, `/aircraft.json`) and remembers the one that
answered.

This is the best long-term source: no rate limits, no terms of service, no
internet dependency, and the lowest latency available.

**Only HTTP JSON feeds are supported.** Raw Beast binary and serial decoding
need a full Mode S decoder and are out of scope; the provider contract is shaped
so they could be added without changing it.

One failure mode gets special handling. If a receiver's decoder stops while its
web server keeps running, the served file still parses perfectly and every
aircraft still looks live. The app compares the receiver's own `now` timestamp
against the local clock and refuses data more than two minutes behind, saying
the decoder may have stopped — rather than showing hours-old aircraft as
current.

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

## Packaging

Both architectures package locally with no Visual Studio installed:

```powershell
dotnet publish apps/windows-desktop/src/OpenFlightDisplay.App/OpenFlightDisplay.App.csproj `
  -c Release -r win-x64 -p:Platform=x64 `
  -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false
```

Swap `win-arm64` / `ARM64` for the other target. Output lands in
`src/OpenFlightDisplay.App/AppPackages/`. Verified sizes: **88.8 MB (x64)** and
**86.3 MB (ARM64)**, self-contained including the Windows App SDK runtime.

### Sideloading

An **unsigned package cannot be installed**. Windows requires the signing
certificate to be trusted on the target machine, so the CI artifacts are for
inspection and for signing downstream, not for direct installation. To install
locally you need a certificate whose subject matches the `Identity/@Publisher`
in `Package.appxmanifest` (`CN=OpenFlightDisplay` by default):

```powershell
# Create a self-signed certificate for local testing only.
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=OpenFlightDisplay" `
  -KeyUsage DigitalSignature -FriendlyName "OFD dev" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# Trust it, then sign and install the package. Trusting a self-signed
# certificate is a real change to the machine's trust store - do it knowingly,
# and remove the certificate when finished testing.
```

Then build with `-p:AppxPackageSigningEnabled=true` and
`-p:PackageCertificateThumbprint=<thumbprint>`.

**No certificate, thumbprint or password is committed to this repository**, and
`.gitignore` excludes `*.pfx`, `*.snk`, `*.cer` and `*.p12`.

### CI

`.github/workflows/windows-desktop.yml` runs on `windows-latest`, path-filtered
to `apps/windows-desktop/**`: restore, build, test with a `.trx` logger and
coverage collection, publish test results as an artifact, then package x64 and
ARM64 in a matrix and upload each `.msix`. The packaging step fails loudly if no
package was produced, rather than uploading an empty artifact.

`.github/workflows/windows-desktop-release.yml` runs on a
`windows-desktop-v*` tag or manual dispatch. Signing is **optional**: with
`WINDOWS_CERT_BASE64` and `WINDOWS_CERT_PASSWORD` configured it signs, and
without them it still produces unsigned packages and warns. The certificate is
written to the runner's temp directory rather than the workspace, and removed in
an `always()` step so it cannot outlive a failed build.

> **Not yet verified by an actual run.** Both workflows are valid YAML and the
> `dotnet` commands in them are the ones proven locally, but no GitHub Actions
> run has executed them. In particular the pinned action versions
> (`checkout@v7`, `setup-dotnet@v5`, `upload-artifact@v7`) have not been
> resolved against the registry from here. Expect the first run to need
> adjustment.

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

### Radar label density (mitigated)

At roughly a hundred aircraft the callsign labels overlapped into an unreadable
mass — seen with a live adsb.lol feed returning 106 aircraft. The plot now draws
at most 200 symbols and 40 labels, nearest first, and says on screen when it is
showing a subset: silently omitting aircraft would make the radar disagree with
the flight board with no explanation. Unlabelled symbols stay selectable and
carry their identity in the tooltip.

A user-configurable density control is still not built.

### Performance at 1,000 aircraft (fixed — measured)

Previously listed here as a suspected flight-board problem. Measuring it showed
that guess was wrong twice over, which is worth recording:

| Attempt | Result at 1,000 aircraft |
|---|---|
| Baseline (clear and rebuild rows) | 112% of one core, +285 MB / 25 s |
| Keyed row reconcile, O(n) | 112%, +285 MB — **no change** |
| Capping radar symbols at 200 | 112%, +285 MB — **no change** |
| **Coalescing the radar redraw** | **18% of one core, +3 MB / 25 s** |

The real cause was `RadarView` redrawing its entire visual tree from every
`CollectionChanged` event. A poll touching 1,000 aircraft raises up to 1,000 of
those, so the plot was rebuilding itself a thousand times per update. Coalescing
onto the dispatcher collapses a batch into one redraw.

The row reconcile and the symbol cap were kept — both are correct improvements
and the cap also fixes label collision — but neither was the bottleneck. Memory
is now flat over a run rather than climbing.

Still to measure: sustained multi-hour operation, and behaviour across sleep and
resume.

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
