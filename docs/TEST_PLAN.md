# Test Plan

## Honesty note (read this first)

This Phase 1 implementation session started with `git` available but no working `node`/`npm`, `platformio`, `python`, or `g++` toolchain in the sandbox — the code below was initially written and only manually reviewed. Node.js, Python, PlatformIO, and a MinGW-w64 GCC toolchain were then installed mid-session (via `winget`/`pip`), and **every test suite and build listed below has since actually been run**, with real bugs found and fixed along the way.

A physical M5Stack Core2 was then connected and used for genuine end-to-end hardware testing: full first-boot provisioning, pairing with a real gateway, live setup via the tablet PWA on an actual phone, and real aircraft data from adsb.lol. This surfaced several real bugs that no amount of code review, native testing, or even a successful `pio run` build could have caught — see the git log for the full list, but the highlights: CORS was missing on both the firmware's and the gateway's HTTP APIs (every cross-origin fetch() from the PWA was silently blocked with no indication it was CORS); the QR pairing code's in-app camera scan is fundamentally non-functional over plain HTTP (`navigator.mediaDevices` requires a secure context, which this LAN-over-HTTP system doesn't have by design); the setup wizard lost all progress on a mobile browser tab reload; numeric form inputs got stuck on `0` and couldn't accept negative longitude; a naive URL string-replace produced malformed gateway URLs; a genuine ESP32 stack overflow in the WebSocket client took three rounds of real diagnosis (including a symbolicated crash backtrace) to fix, ultimately requiring the WS client to run in its own dedicated FreeRTOS task; the gateway's `.env` file was never actually being loaded (silently defaulting to the mock provider the entire session); and a burst of rapid WebSocket reconnects crashed the entire gateway process via an unhandled promise rejection from a file-write race condition.

Current status: **all 199 automated tests pass** (protocol 10, shared-models 20, gateway 26, tablet-pwa 38, firmware native 105), typecheck/lint/build are clean across every TypeScript workspace, and the real ESP32 firmware builds, flashes, and **runs stably on physical hardware with live aircraft data flowing continuously with no crashes**. What's still not done: Playwright end-to-end tests (no browser-automation tool was available even after installing the rest of the toolchain), and a multi-day continuous-operation/heap-leak soak test (see item 8 below).

> An earlier revision of this file claimed 107 total / 34 firmware tests. The firmware count had gone stale as suites were added and was never corrected; every number here is now obtained by running the suites rather than by editing the previous figure. Recent movement: 144 → 199, from the flight-tracking work (34 new native tests, 12 new PWA tests, 9 new shared-model tests).

**Board coverage:** every automated test here is board-independent — the native suites exercise `domain/`, which by design contains no board or Arduino code at all. Neither the `core2` nor the `tab5` firmware build is exercised in CI (see `.github/workflows/firmware-native-tests.yml` for why), and **no automated test touches the Tab5 in any way**. What backs the Core2 is the manual hardware validation below; the Tab5 has no equivalent yet — see `docs/TAB5_HARDWARE.md`.

## Firmware tests (`firmware/display/test/native`)

Domain logic lives in hardware-independent C++ (`firmware/display/{include,src}/domain/`) specifically so it can be tested without an ESP32 or PlatformIO's Arduino simulation — just a native compiler.

Run with:
```
cd firmware/display
pio test -e native
```

All 105 test cases across the 8 suites below pass.

Covered in Phase 1:
- Haversine distance and bearing calculation, including degenerate cases (same point, antipodal-ish points).
- Nearest-aircraft ranking over a bounded aircraft array.
- Monitoring-area (circular) inclusion test, including boundary (exactly-at-radius) cases.
- Config parsing/validation, including a deliberately corrupt payload (must fail closed, not crash).
- Stale-record detection (age threshold behavior).
- WebSocket aircraft-update message parsing, including a bounded/truncated payload.

Flight tracking (`test_flight_tracking`, 27 cases) covers the logic nobody can debug from an airport car park:
- ADS-B callsign padding (`"BAW249  "`) trimmed before any comparison.
- Flight-number normalization: IATA→ICAO expansion (`UA1234` → `UAL1234`), case/separator handling, pass-through of an unrecognised carrier rather than mangling it, rejection of input with no digits.
- Phase transitions, including the three that are easy to get wrong and expensive when wrong:
  - **A fast, high overflight of the destination is not a landing** — touchdown requires near *and* slow *and* low together.
  - **Height is measured above field elevation, not sea level** — otherwise every arrival at Denver reads as landed while still airborne.
  - **Landed beats lost-contact** — an aircraft that goes quiet *at the field* has arrived; one that goes quiet mid-ocean has not, and conflating them sends someone to the airport an hour early.
- ETA edge cases: a stationary aircraft has a distance but no ETA (no division by zero), and no destination yields neither.
- The adaptive poll interval tightens monotonically as arrival approaches, and stays within bounds in every phase.

Tracked-flight config (`test_config`, 7 of its 15 cases) covers the three-way wire semantics: an absent key preserves existing tracking (so a brightness-only PUT doesn't cancel someone's airport run), an explicit null clears it, IATA airport codes are rejected rather than guessed at, and the whole thing survives a serialize round-trip.

Deferred to Phase 2+ (documented, not implemented): polygon/cone boundary tests, multi-area tests, unit-conversion tests, touch-interaction tests, low-memory/heap-pressure tests, LittleFS-write-failure simulation, WPA3 connection tests. These require either more display/filter features to exist first, or physical hardware to observe real memory behavior.

## Gateway tests (`services/gateway/tests`)

Run with:
```
cd services/gateway
npm install
npm test
```

All 26 tests across 5 suites pass.

Covered in Phase 1:
- Mock provider normalization → `AircraftState[]` shape and required-field validation.
- Replay provider: deterministic playback of a fixture file.
- adsb.lol adapter: normalization from a recorded sample response fixture (does not hit the network in tests) — separately confirmed against the live API by hand (see `docs/DATA_SOURCE_EVALUATION.md`).
- Nearest-distance ranking given a `MonitoringArea`.
- REST endpoint validation (pairing, config CRUD) — rejects malformed bodies, enforces pairing-token requirement on writes.
- WebSocket contract test: client receives a versioned envelope, a heartbeat, and a provider-status message on simulated provider failure.
- Config persistence round-trip (write, restart the store, read back identical config).
- A burst of 20 concurrent writes to the same device record, none of which reject (regression test for a real crash found via hardware testing — a file-write race condition that took down the whole gateway process; see the git log).

Deferred: rate-limit-exhaustion behavior against a real provider, multi-client WS fan-out under load, SQLite migration tests (no SQLite until Phase 4).

## PWA tests (`apps/tablet-pwa/tests`)

Run with:
```
cd apps/tablet-pwa
npm install
npm test
```

All 38 tests across 7 suites pass.

Covered in Phase 1:
- Setup-wizard step transitions (pairing → location → radius → confirm), including manual-entry and mocked-geolocation paths, and resuming from a persisted mid-wizard state.
- Map + info card rendering against mock aircraft data.
- Status-banner state transitions (connecting, connected, stale, source-unavailable, wifi-down-equivalent/gateway-unreachable).
- Config persisted to and rehydrated from `localStorage`, confirming Wi-Fi credentials are never touched by the PWA.
- URL normalization/validation helpers (`lib/url.ts`) — regression tests for a real bug where a bare `ip:port` (no scheme) silently produced a malformed `ws://` URL.
- Tracked-flight panel: the flight number is sent verbatim (IATA expansion is the device's job, so there is only one airline table), an IATA airport code is rejected before any request is made, no-ETA renders as a dash rather than `0` ("landing now"), and lost signal is explicitly described as not a landing.

Deferred: Playwright end-to-end tests (no browser-automation toolchain in this sandbox this session). Camera-based QR scanning is mocked in unit tests, and separately confirmed via real hardware testing to be **fundamentally non-functional** in this system's normal (plain-HTTP-over-LAN) deployment, since `navigator.mediaDevices` requires a secure context — manual entry is the PWA's default and primary pairing path as a result, not a fallback. Full kiosk-mode wake-lock behavior is still browser-dependent and unverified.

## Replay fixtures (`services/gateway/tests/fixtures`)

Phase 1 ships a minimal fixture set (expand per `docs/IMPLEMENTATION_PLAN.md` as later phases need more scenarios):
- No aircraft in range.
- One commercial aircraft.
- Multiple mixed aircraft (used for future ranking-mode tests).
- Stale-position aircraft (timestamp older than the staleness threshold).
- Provider-outage simulation (adapter throws/times out).

Fixtures are synthetic/sanitized — no real registrations tied to identifiable private owners, consistent with `docs/SECURITY_AND_PRIVACY.md`.

## CI (`.github/workflows`)

- `firmware-native-tests.yml` — installs PlatformIO, runs `pio test -e native`. Does not require physical hardware.
- `gateway.yml` — `npm ci`, lint, typecheck, `npm test`, `npm run build`.
- `pwa.yml` — `npm ci`, lint, typecheck, `npm test`, `npm run build`.
- `protocol-typecheck.yml` — typechecks `packages/shared-models` and `packages/protocol` in isolation, since both `services/gateway` and `apps/tablet-pwa` depend on them.

Explicitly **not** included yet (Phase 5): signed release artifacts, hardware-in-the-loop flashing, long-duration soak tests. A `docs/RELEASE_PROCESS.md` will be written when that phase starts.

## Manual/hardware validation

Performed against a real M5Stack Core2 (ESP32-D0WDQ6-V3, 16MB flash, no PSRAM) and a real gateway/PWA on the same LAN:

1. **Done.** Flash firmware, confirm boot screen appears. (Note: the auto-reset sequence after `pio run -t upload` did not reliably bring the board out of the ROM bootloader's download mode on this unit/cable combination — a manual power-cycle was needed after every flash. Worth knowing if a future flash "looks stuck" showing "waiting for download.")
2. **Done.** SoftAP + captive portal appears when no Wi-Fi is configured; real credentials entered and connected successfully; config persisted across reboots.
3. **Done, with a real finding.** The QR code renders correctly, but scanning it two different ways both failed as originally designed: a phone's default camera app opens it as a plain link (fixed by adding a GET handler that explains what to do instead of a dead page), and the PWA's own in-app camera scanner cannot work at all over plain HTTP (`navigator.mediaDevices` requires a secure context — see docs/ARCHITECTURE.md). **Manual IP/code entry is the pairing method that actually works** and is now the PWA's default, not a fallback.
4. **Done, with real findings.** Pairing and config both reached the Core2 successfully, but only after fixing: missing CORS on the Core2's own HTTP API, missing CORS on the gateway's API, a stuck-at-`0`/no-negative-numbers bug in the location form, a malformed gateway WebSocket URL from a naive string replace, and a response-shape mismatch between the PWA and the Core2's config endpoint.
5. **Done.** With the gateway pointed at `adsb.lol` and a real location, real moving commercial traffic (multiple distinct real flights, confirmed against the live API) appeared on both the Core2 and the PWA within one polling interval of each other.
6. **Not done.** Pulling the Wi-Fi router's power was not tested (would disrupt the tester's home network) — `WifiState::Disconnected` handling is implemented and native-logic-adjacent paths are tested, but this exact scenario is unverified on real hardware.
7. **Done.** Stopping the gateway process showed "Data source unavailable" on the Core2 (not a crash or frozen screen) exactly as designed; restarting the gateway reconnected automatically within about a second with no manual intervention, and the display correctly returned to an accurate live state ("No matching aircraft" when that was in fact true at that moment, not stale cached data).
8. **Not done.** No multi-day continuous-operation/heap-growth soak test has been run yet. Worth prioritizing given how many real concurrency/stack issues turned up in just a few hours of interactive testing (see the git log) — a long unattended run could surface more.

Two crashes were found and fixed as a *direct result* of this checklist, neither of which any prior review or automated test had caught: a stack overflow in the ESP32's WebSocket handling (found while validating item 5) and an unhandled-promise-rejection crash in the gateway's device store from a file-write race (found while validating item 7). Both now have regression coverage (native/gateway tests) where the underlying logic allows it, and are documented in detail in the relevant source files and git commits.
