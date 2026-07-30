# Implementation Plan

## Phase 0 — Discovery and design (this session, complete)

- Inspected the working directory: no existing OpenFlightDisplay repo, only unrelated projects.
- Reviewed reference-project licenses and aviation-data-provider terms (see `docs/ATTRIBUTION.md`, `docs/DATA_SOURCE_EVALUATION.md`).
- Produced the required documentation set and the feature parity matrix.
- Chose the smallest viable vertical slice (below) and the concrete tech stack per component.

## Phase 1 — Core vertical slice (this session)

Concrete steps, in commit order:

1. Repo skeleton, LICENSE, `.gitignore`, `CONTRIBUTING.md`, `SECURITY.md`. **(done)**
2. Documentation set (this file and its siblings). **(done)**
3. `packages/shared-models` + `packages/protocol` — the normalized data model and versioned wire protocol used by everything downstream.
4. `services/gateway` — mock/replay/adsb.lol provider adapters, normalization, nearest-distance ranking, REST (pairing/config/status), WebSocket aircraft feed, JSON-file config persistence.
5. `firmware/display` domain layer — hardware-independent C++ (distance/bearing, ranking, config validation, staleness, message parsing) with native PlatformIO tests.
6. `firmware/display` app layer — Wi-Fi provisioning, pairing HTTP server, LittleFS persistence, aircraft data source, single-aircraft render + explicit status screens, behind a compile-time board layer so one tree builds for every supported board.
7. `apps/tablet-pwa` — setup wizard, basic map + info card, status banner, offline shell, localStorage persistence.
8. CI workflows (build/test only, no releases yet).
9. Root README tying it together, with real setup instructions for each component.

Acceptance criteria: `docs/PRODUCT_REQUIREMENTS.md` § "Phase 1 vertical-slice acceptance criteria."

## Phase 2 — Nearby-aircraft product (future session)

- Multiple aircraft (compact list mode on Core2).
- Filtering (altitude, on-ground, category) and additional ranking modes.
- Airline/aircraft enrichment (cached, license-tracked datasets).
- Display-profile editing from the PWA; Core2 layout preview in the PWA.
- Saved configurations; configurable units (imperial/aviation); brightness/night controls.
- Map pin placement and address/geocoding for location entry.

## Phase 3 — Advanced areas and tracking (future session)

- Polygon and directional-cone monitoring areas; multiple named areas.
- Individual-flight tracking (by flight #, callsign, registration, ICAO hex) and the tracked-flight Core2/PWA modes.
- Favorites/excluded aircraft.
- Airport mode (with explicit "position-only, not schedule data" labeling where the provider lacks schedule data).
- Tablet split view (radar + board/detail).
- Alerts (area entry, type, favorite, tracked-flight detected, emergency squawk, etc.) across Core2 screen/speaker/vibration and browser notifications, with per-alert cooldowns.

## Phase 4 — Local ADS-B and gateway hardening (future session)

- `readsb`/`dump1090` and `tar1090` adapters (LAN-only, no TLS needed — the ideal long-term data source).
- Dockerized gateway deployment (Raspberry Pi / Home Assistant add-on / generic Linux).
- SQLite-backed history; provider failover; replay-mode-as-a-service for demos.
- MQTT + Home Assistant discovery documentation.

## Phase 5 — Production hardening (future session)

- OTA firmware updates with rollback strategy.
- Multi-device fleet management from the PWA.
- Long-duration (multi-day) continuous-operation testing, memory profiling, load testing.
- API-key management UX; config import/export.
- Accessibility audit; localization foundation.
- Signed release artifacts and a documented release process (a `docs/RELEASE_PROCESS.md` will be written at that point).

## Explicit non-goal for every phase

No phase attempts to reproduce any commercial product's specific branding, visual design, or proprietary source. See `docs/ATTRIBUTION.md`.

## Session limitation (read before trusting "done" claims)

This implementation session started with `git` available but no `node`/`npm`, `platformio`, or working `python`/`g++` in the sandbox, so Phase 1 code was initially only hand-written and reviewed. Node.js, Python, PlatformIO, and a MinGW-w64 GCC toolchain were then installed mid-session, and everything has since actually been run: all 99 automated tests pass, every TypeScript workspace typechecks/lints/builds cleanly, and the real ESP32 firmware target builds and links. Several real bugs were found and fixed in that process (see git log). What remains unverified is physical-hardware runtime behavior and Playwright end-to-end tests — see `docs/TEST_PLAN.md` for the exact breakdown.
