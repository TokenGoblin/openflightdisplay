# Test Plan

## Honesty note (read this first)

This Phase 1 implementation session had `git` available but no working `node`/`npm`, `platformio`, `python`, or `g++` toolchain in the sandbox. Every test file listed below was **written and manually reviewed**, but **none were executed in this session**. Nothing here is claimed as "passing" from this session — only "present, and expected to pass once run." Run the commands in each section yourself (or let the CI workflows in `.github/workflows` run them) before trusting this code.

## Firmware tests (`firmware/core2/test/native`)

Domain logic lives in hardware-independent C++ (`firmware/core2/{include,src}/domain/`) specifically so it can be tested without an ESP32 or PlatformIO's Arduino simulation — just a native compiler.

Run with:
```
cd firmware/core2
pio test -e native
```

Covered in Phase 1:
- Haversine distance and bearing calculation, including degenerate cases (same point, antipodal-ish points).
- Nearest-aircraft ranking over a bounded aircraft array.
- Monitoring-area (circular) inclusion test, including boundary (exactly-at-radius) cases.
- Config parsing/validation, including a deliberately corrupt payload (must fail closed, not crash).
- Stale-record detection (age threshold behavior).
- WebSocket aircraft-update message parsing, including a bounded/truncated payload.

Deferred to Phase 2+ (documented, not implemented): polygon/cone boundary tests, multi-area tests, unit-conversion tests, touch-interaction tests, low-memory/heap-pressure tests, LittleFS-write-failure simulation, WPA3 connection tests. These require either more display/filter features to exist first, or physical hardware to observe real memory behavior.

## Gateway tests (`services/gateway/tests`)

Run with:
```
cd services/gateway
npm install
npm test
```

Covered in Phase 1:
- Mock provider normalization → `AircraftState[]` shape and required-field validation.
- Replay provider: deterministic playback of a fixture file.
- adsb.lol adapter: normalization from a recorded sample response fixture (does not hit the network in tests).
- Nearest-distance ranking given a `MonitoringArea`.
- REST endpoint validation (pairing, config CRUD) — rejects malformed bodies, enforces pairing-token requirement on writes.
- WebSocket contract test: client receives a versioned envelope, a heartbeat, and a provider-status message on simulated provider failure.
- Config persistence round-trip (write, restart the store, read back identical config).

Deferred: rate-limit-exhaustion behavior against a real provider, multi-client WS fan-out under load, SQLite migration tests (no SQLite until Phase 4).

## PWA tests (`apps/tablet-pwa/tests`)

Run with:
```
cd apps/tablet-pwa
npm install
npm test
```

Covered in Phase 1:
- Setup-wizard step transitions (pairing → location → radius → confirm), including manual-entry and mocked-geolocation paths.
- Map + info card rendering against mock aircraft data.
- Status-banner state transitions (connecting, connected, stale, source-unavailable, wifi-down-equivalent/gateway-unreachable).
- Config persisted to and rehydrated from `localStorage`, confirming Wi-Fi credentials are never touched by the PWA.

Deferred: Playwright end-to-end tests (no browser-automation toolchain in this sandbox this session — write these in a follow-up session with the tool available), true camera-based QR scan (mocked in unit tests instead), full kiosk-mode wake-lock behavior (browser-dependent, needs manual verification on target tablets).

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

Explicitly **not** included yet (Phase 5): signed release artifacts, hardware-in-the-loop flashing, long-duration soak tests. See `docs/RELEASE_PROCESS.md`.

## Manual/hardware validation (required before calling Phase 1 "hardware-verified")

None of this was performed in this session — it requires a physical M5Stack Core2. Documented here so whoever has the hardware knows exactly what to check:

1. Flash firmware (`pio run -e core2 -t upload`), confirm boot screen appears.
2. Confirm SoftAP + captive portal appears when no Wi-Fi is configured; enter real credentials; confirm it connects and persists across a power cycle.
3. Confirm the QR code renders correctly and scans successfully from the PWA on an actual tablet.
4. Confirm pairing completes and location/radius set from the PWA reach the Core2.
5. With the gateway running and adsb.lol reachable, confirm a real nearby aircraft (if any are in range at test time) appears on both Core2 and PWA within one polling interval of each other.
6. Pull the Wi-Fi router's power; confirm the Core2 shows "Wi-Fi disconnected," not a crash or blank screen; confirm it reconnects automatically when Wi-Fi returns.
7. Stop the gateway process; confirm the Core2 shows "Data source unavailable" (or similar) rather than freezing on stale data silently.
8. Leave it running for at least 24 hours; check for heap growth (log `ESP.getFreeHeap()` periodically) as a basic leak check.
