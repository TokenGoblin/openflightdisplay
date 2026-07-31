# ADR 0001: Windows Desktop Application Architecture

- **Status:** Accepted
- **Date:** 2026-07-31
- **Deciders:** Windows desktop port working group
- **Supersedes:** none
- **Related:** `docs/ARCHITECTURE.md`, `docs/PROTOCOL.md`, `docs/WINDOWS_DESKTOP.md`

## Context

OpenFlightDisplay currently ships three components: ESP32 firmware (`firmware/display`, C++), a
gateway service (`services/gateway`, TypeScript/Node), and a tablet PWA (`apps/tablet-pwa`,
React/TypeScript), with shared TypeScript contracts in `packages/`.

We are adding a fourth client — **OpenFlightDisplay Desktop**, a native Windows application —
at `apps/windows-desktop/`. It is not a reskin of the PWA. A desktop has orders of magnitude
more CPU, GPU, memory and pixels than an ESP32 or a tablet browser, and the intent is that this
becomes the most capable client in the project.

### Verified starting conditions

Everything below was measured on the target development machine, not assumed:

| Fact | Evidence |
|---|---|
| Baseline is green | 217/217 tests pass on a clean clone (104 firmware native + 113 TypeScript) |
| Lint / typecheck clean | `npm run lint` and `npm run typecheck` both exit 0 |
| `npm test` fails on a *fresh* clone | Workspace packages have no `dist/`; vite cannot resolve `@openflightdisplay/protocol`. `npm run build` first is mandatory. Build-order issue, not a regression |
| .NET 10 SDK | 10.0.302, runtimes `Microsoft.NETCore.App 10.0.10` / `Microsoft.WindowsDesktop.App 10.0.10` |
| Windows App SDK latest stable | **2.3.1** (NuGet flat-container query; 2.3.2 and 2.2.2 are `-experimental`, excluded) |
| WinUI 3 builds with **no Visual Studio** | Probe project built clean — XAML compiler resolves entirely through NuGet targets |
| MSIX packaging works with no Visual Studio | Probe produced `probe_0.1.0.0_x64.msix` (27 MB) plus `Microsoft.WindowsAppRuntime` dependency bundles |
| ARM64 builds | `dotnet build -r win-arm64` exits 0 |
| Symbol packages do **not** build | `mspdbcmf.exe` absent (ships with the VS C++ toolset) — `.appxsym` generation is skipped with a warning |

The last row is the only toolchain gap and it is non-blocking; see "Consequences".

## Decision

### 1. Clean architecture with a UI-free domain core

Seven source projects. The dependency rule is one-way — `Core` depends on nothing but the BCL:

```
        ┌──────────────────────────────────────────┐
        │        OpenFlightDisplay.App             │  WinUI 3 / XAML / MVVM
        └────────────────┬─────────────────────────┘
                         │
   ┌────────────┬────────┴────────┬───────────────┬──────────────┐
   │            │                 │               │              │
Providers  Infrastructure    Persistence   DeviceProtocol       Map
   │            │                 │               │              │
   └────────────┴────────┬────────┴───────────────┴──────────────┘
                         │
                ┌────────▼─────────┐
                │       Core       │  no WinUI, no HTTP, no SQLite, no Win32
                └──────────────────┘
```

`Core` holding no I/O is what makes the parity fixtures (below) meaningful: the same
deterministic functions the firmware implements in C++ can be tested in isolation, with no
network or database in the way.

### 2. The desktop app is standalone-first; the gateway is optional

The firmware already polls providers directly and works with no gateway (`docs/ARCHITECTURE.md`).
The desktop follows the same principle, running the whole pipeline in-process:

```
Provider → Normalization → Filtering → Ranking → History → App state → UI
```

Gateway mode is retained as a *selectable data mode*, not a dependency, because it still earns
its place for shared upstream polling, existing installations, fleet coordination, and
server-held credentials.

Five data modes: `Direct provider`, `Gateway`, `Local ADS-B receiver`, `Mock`, `Replay`.

**Consequence: no Node.js runtime is required for normal Windows use.** Node remains required
only to develop the gateway/PWA.

### 3. Reconstruct the domain idiomatically; do not transliterate

The TypeScript and C++ domain logic is small, well-commented and genuinely portable. It will be
rebuilt in idiomatic C# — records, `readonly struct`, nullable reference types, discriminated
unions via sealed hierarchies — *preserving observable behavior*, not syntax.

Concretely, the following are already duplicated across TS and C++ and become a third
implementation in C#:

| Algorithm | TypeScript | C++ | C# target |
|---|---|---|---|
| Haversine distance | `services/gateway/src/lib/geo.ts` | `firmware/display/src/domain/geo.cpp` | `Core/Geo/GeoMath.cs` |
| Initial bearing | same | same | same |
| Nearest ranking | `services/gateway/src/lib/ranking.ts` | `firmware/display/src/domain/ranking.cpp` | `Core/Ranking/` |
| Staleness | `ranking.ts` (`STALE_POSITION_THRESHOLD_MS`) | `domain/staleness.cpp` | `Core/Quality/Staleness.cs` |
| Flight phase / ETA / departure advice | — (device-only) | `domain/flight_tracking.cpp` | `Core/Tracking/` |
| adsb.lol normalization | `providers/adsblol.ts` | `app/adsb_provider.cpp` | `Providers/AdsbLol/` |

Three implementations of one algorithm is a real risk. Mitigation: **shared JSON parity
fixtures** at `datasets/parity/`, consumed by the C# xUnit suite and (where practical) the
existing TS and C++ suites, so divergence fails a build rather than surfacing in the field.

Fixtures must cover: geographic distance, bearing, aircraft normalization, nearest ranking,
staleness, malformed provider data, incomplete records, tracked-flight phases, ETA, departure
advice, and explicit source-error states.

### 4. Constants that are load-bearing and must not drift

Ported verbatim, asserted by fixture:

- `EARTH_RADIUS_KM = 6371.0088`
- `STALE_POSITION_THRESHOLD_MS = 60_000` — boundary is *not* stale exactly at threshold
- `MAX_AIRCRAFT_PER_UPDATE = 10` — wire bound only; **the desktop's own UI is not capped at 10**
- adsb.lol radius clamp: gateway 250 NM, firmware 80 NM. Desktop adopts **250 NM** — it has real
  memory, and the firmware's 80 NM exists solely to protect a fixed 16 KB parse buffer
- ETA cap of 1440 minutes; groundspeed floor of 1.0 kt before dividing
- Landing requires near-field **and** slow **and** low against *destination elevation*

### 5. Wire protocol is frozen at `schemaVersion: 1`

The desktop is a *new client of an existing contract*. It introduces no protocol change, so no
version bump, and `docs/PROTOCOL.md` needs no edit for this work. Should a later phase require
one, the existing policy applies: bump, preserve backward compatibility, update `PROTOCOL.md`,
and update tests in **every** implementation (TS, C++, C#).

The desktop must honor the binding rules already in `PROTOCOL.md`: reject unknown
`schemaVersion` rather than guess-parse; 15 s heartbeat with 45 s death detection; reconnect
with exponential backoff + jitter (base 1 s, cap 30 s); `provider-status: unavailable` must
**not** clear the last-known aircraft list.

A new `role` value is required for the `hello` message. Per `PROTOCOL.md` this is an additive
optional change and does **not** bump `schemaVersion`, but it must be added in three places:
`packages/protocol/src/wsMessages.ts`, the C# client, and the gateway's validation.

### 6. WebView2 for the map only, behind a typed bridge

WinUI 3 has no first-class map control. Options considered:

| Option | Verdict |
|---|---|
| `MapControl` (Windows.UI.Xaml.Controls.Maps) | Rejected — WinUI 3 support is poor and it wants a Bing key |
| Custom Win2D/SkiaSharp renderer + raster tiles | Deferred — best long-term, largest cost |
| **WebView2 + MapLibre GL** | **Chosen** for the interactive map surface |

Constraint, restated from the brief and binding: the map is an **isolated rendering component**.
No business logic and no application state in JavaScript. The bridge is a typed C# surface
passing plain DTOs; the JS side renders what it is told and reports user gestures back. If the
bridge starts growing behavior, that is the signal to build the native renderer instead.

The aircraft symbology layer (heading-oriented symbols, range rings, trails, labels) is drawn in
**native WinUI over** the map surface where feasible, so selection, hit-testing and accessibility
stay in C#.

### 7. Aviation-data honesty is an architectural constraint, not UI copy

The existing project is careful about this and the desktop must not regress it. Five *separate*
concepts, never merged in a model or a view:

1. observed ADS-B position
2. calculated estimate
3. airport metadata
4. airline/aircraft enrichment
5. published schedule information

Origin, destination, gate and scheduled arrival are **never fabricated**. ADS-B carries no
timetable. Calculated values carry their provenance in the model itself — a computed ETA is
typed such that a view cannot render it without also having the "estimated from current
position and groundspeed" qualifier and the data age available.

Loss of ADS-B reception is `LostContact`, never `Landed`. This is already correct in the
firmware and is a regression risk worth naming explicitly.

Every failure state is explicit. **No indefinite spinner** — the project's existing hard rule.

### 8. SQLite via `Microsoft.Data.Sqlite`, history off by default

Chosen over EF Core: the schema is small, the write path is a high-frequency append that
benefits from explicit control, and avoiding an ORM keeps `Persistence` free of a large
dependency for little gain. Hand-rolled forward-only migrations with a `schema_version` table.

History is **disabled by default** and disclosed during onboarding. All data stays local unless
the user explicitly exports.

### 9. Secrets in Windows Credential Manager, never in config files

Provider API keys go to the OS credential store. Settings files hold a *reference*, never a
secret. Rolling logs are redacted.

## Alternatives rejected

- **Electron / Blazor Hybrid / packaged browser** — excluded by the brief; also defeats the
  point of a native client.
- **.NET MAUI / Avalonia / WPF / WinForms** — excluded by the brief. MAUI's Windows target is
  WinUI underneath with extra layers; WPF is mature but not the modern Windows-native stack.
- **Fullscreen WebView hosting the existing PWA** — explicitly forbidden, and would inherit
  every browser limitation the desktop exists to escape.
- **Node.js sidecar** — forbidden for normal operation; would reintroduce the runtime dependency
  the port is meant to remove.
- **Forking the repo instead of extending the monorepo** — rejected. Nothing about a language
  port preserves useful `git blame`, but the *shared contracts* (`docs/PROTOCOL.md`, parity
  fixtures) are the whole point of staying in one tree. Divergence would be silent.

## Consequences

### Positive

- Domain logic becomes testable without a device, a network, or a browser.
- Gateway stops being a hard dependency for a rich client.
- Parity fixtures convert a real triplication risk into a build failure.
- No Node.js needed to run the Windows app.

### Negative / accepted costs

- **A third implementation of the same domain algorithms.** Mitigated by fixtures, not
  eliminated. This is the single largest maintenance cost of the decision.
- **WebView2 is a browser dependency for the map**, and the thing we said we wanted to avoid —
  narrowly scoped and gated behind the "no logic in JS" rule, with a native renderer as the exit.
- **No symbol packages in this environment.** `mspdbcmf.exe` ships with the VS C++ toolset,
  which is absent. CI on `windows-latest` has it, so `.appxsym` generation should be enabled
  there and is simply unavailable for local dev builds. Documented, not worked around.
- WinUI 3's accessibility story requires deliberate effort; it is not free. Budgeted in Phase 4.

### Neutral

- ARM64 is a build target from day one and cannot be validated on this x64 machine beyond
  "it compiles". Runtime ARM64 verification is an explicit open item, in the same spirit as the
  project's existing honesty about the untested Tab5.

## Open questions

1. Whether the native-overlay symbology layer stays performant at 1,000 aircraft, or whether the
   symbol layer must move into the map renderer. **Measure before deciding** — the brief requires
   measurement over guessing, and the diagnostics page exists to supply the numbers.
2. Whether local receiver support can reach raw Beast/serial decoding, or stays at HTTP JSON
   (dump1090/readsb/tar1090). First milestone is HTTP JSON only; interfaces designed so the
   others can be added without reshaping the provider contract.
3. Whether device LAN discovery can use mDNS from a packaged MSIX app without a capability that
   trips Store certification. Manual IP entry is the guaranteed path regardless — and per the
   existing README, manual entry is already the pairing method that actually works.
