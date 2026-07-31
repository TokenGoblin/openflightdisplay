# OpenFlightDisplay

An open-source aircraft-tracking display system for M5Stack devices and a tablet Progressive Web App — see nearby air traffic at a glance, on a compact always-on display and/or a larger tablet radar/flight-board view.

OpenFlightDisplay is an **original implementation**, inspired by the general "home flight-tracking display" product category (products like TheFlightWall) but not a fork, clone, or port of any commercial product's source code, branding, or visual design. See `docs/ATTRIBUTION.md` for exactly what was studied, under what license, and what that does and doesn't permit.

> **Status: Phase 1 vertical slice.** This is a narrow, end-to-end working slice (Wi-Fi provisioning, pairing, one live data provider, single-aircraft display) — not the full feature set described in `docs/PRODUCT_REQUIREMENTS.md`. See `docs/FEATURE_PARITY_MATRIX.md` for what's done vs. planned, and `docs/IMPLEMENTATION_PLAN.md` for the phased roadmap.
>
> **All 199 automated tests pass**, every TypeScript workspace typechecks/lints/builds cleanly, and — this is the real milestone — **a physical M5Stack Core2 has been fully tested end-to-end**: first-boot Wi-Fi provisioning, pairing with a real gateway from an actual phone browser, and live aircraft data from `adsb.lol` (real, moving commercial flights, confirmed over multiple aircraft) rendering continuously on both the Core2's screen and the tablet PWA. Getting there surfaced and fixed several real bugs no amount of code review, native testing, or even a successful build could have caught — including a genuine ESP32 stack overflow, missing CORS on two different HTTP APIs, a gateway `.env` file that was silently never being loaded, and a crash from a file-write race condition — see `docs/TEST_PLAN.md` for the full list. What's still not done: a multi-day soak test, a Wi-Fi-outage test, and Playwright end-to-end browser tests. See "Known limitations" below.

## Product overview

- **Device display**: an always-on airport-departure-board-style screen showing the nearest aircraft to a configured location, with explicit status states (never an indefinite spinner) for every failure mode. One firmware (`firmware/display/`) builds for two boards.
- **Follow a flight**: enter a flight number and arrival airport, and the display becomes a countdown to touchdown — ETA, distance to go, and phase (waiting / enroute / descending / approaching / landed). Built for the "when do I leave to collect someone" case. It uses a direct callsign lookup on a cadence that tightens as the flight nears, and it is careful about what it claims: the ETA comes from current groundspeed, **not** a published schedule, and lost signal is reported as lost signal rather than as an arrival. See `docs/DISPLAY_UI.md`.
- **Tablet PWA**: the setup/configuration surface (pair by typing the device's IP + on-screen code — see "Known limitations" on why QR scanning isn't the primary path — plus a location + radius picker), a basic radar map + flight-info card, and — once a gateway is reachable on the LAN — a display mode that doesn't require a device at all.
- **Gateway**: a small local service that polls a live aviation-data provider, normalizes and ranks aircraft, and serves both displays over a shared local WebSocket feed.

No screenshots yet — no UI/photography review pass has been done this session; see "Known limitations."

## Supported hardware

| Board | Panel | Status |
|---|---|---|
| **M5Stack Core2** (ESP32) | 320×240 capacitive touchscreen | **Hardware-validated** on a real ESP32-D0WDQ6 v3.0 unit (16MB flash, no PSRAM) — full provisioning, pairing and live data |
| **M5Stack Tab5** (ESP32-P4) | 1280×720 MIPI-DSI, 32MB PSRAM | **Compiles only.** No physical unit has been connected. Everything else is unverified |

Both build from one source tree with a per-board layer; `pio run -e core2` and `pio run -e tab5` select the board. The Tab5's larger panel additionally shows a nearby-traffic board alongside the nearest aircraft, using aircraft the provider already fetches and ranks.

- `docs/CORE2_HARDWARE.md` — Core2 memory budget and what's confirmed on real hardware vs. estimated.
- `docs/TAB5_HARDWARE.md` — Tab5 toolchain requirements, the ESP32-C6 Wi-Fi arrangement, and an explicit list of what to check first when a real unit is available.
- `docs/DISPLAY_UI.md` — the on-device airport-FIDS screen design for both boards (layout, fonts/licensing, color tokens, screen states, rendering strategy).

Plus:
- Any modern tablet/phone/desktop browser for the PWA (installable; works standalone without a device as long as a gateway is reachable).
- Any small Linux machine / Raspberry Pi / Docker host / plain laptop to run the gateway.

## Architecture

```
provider (adsb.lol / mock / replay)
        │ HTTPS polling (gateway only — never the ESP32; see docs/ARCHITECTURE.md)
        ▼
services/gateway  ──WS/HTTP (LAN, plaintext)──►  firmware/display (Core2 / Tab5)
        │
        └──WS/HTTP (LAN or same-origin)──►  apps/tablet-pwa (setup + radar + card)
```

Full diagram and rationale (including why provider polling never happens on the ESP32): `docs/ARCHITECTURE.md`.

## Quick start

### 1. Gateway (start here — both other components depend on it)

```
npm install
cp services/gateway/.env.example services/gateway/.env   # defaults to AVIATION_PROVIDER=mock, no external deps
npm run dev --workspace @openflightdisplay/gateway
```

See `services/gateway/README.md` and `docs/PROVIDER_ADAPTERS.md` for switching to a real data source.

### 2. Tablet PWA

```
npm run dev --workspace @openflightdisplay/tablet-pwa
```

Open the printed URL on a tablet/browser on the same LAN as the gateway.

### 3. Device firmware

```
cd firmware/display
pio test -e native          # domain-logic unit tests, no hardware needed
pio run -e core2            # build for the M5Stack Core2
pio run -e tab5             # build for the M5Stack Tab5
pio run -e core2 -t upload -t monitor   # flash + serial monitor (needs a connected board)
```

On Windows, build `tab5` from PowerShell or cmd rather than Git Bash — the ESP32-P4 toolchain installer refuses to run under MSys. See `docs/TAB5_HARDWARE.md`.

See `firmware/display/README.md` and `docs/PROVISIONING.md` for the Wi-Fi setup / pairing flow.

## Supported data sources

| Provider | Role | Notes |
|---|---|---|
| Mock | Dev/test default | No network, synthetic aircraft |
| Replay | Dev/test, demos | Plays back recorded fixtures |
| adsb.lol | **Phase 1 default live provider** | Free, open, no API key currently required |
| airplanes.live | Documented adapter, not default | Non-commercial ToS, 1 req/sec |
| OpenSky Network | Documented, not used for live polling | Daily quota too low for continuous display |
| ADS-B Exchange | Documented optional paid adapter | Community tier is non-commercial only |
| Local dump1090/tar1090 | Documented, planned Phase 4 | Best long-term option if you run your own receiver |

Full evaluation, terms, and rationale: `docs/DATA_SOURCE_EVALUATION.md`.

## Privacy

Local-first, no mandatory account, no telemetry/ads/analytics SDKs anywhere. Your approximate location is used to query your chosen aviation-data provider (disclosed, not hidden) but never sent anywhere else. Wi-Fi credentials never leave the Core2 — they're entered once directly into its own temporary access point. Full details: `docs/SECURITY_AND_PRIVACY.md`.

## Known limitations (read before flashing hardware or deploying)

- **Tab5 support is unproven.** The firmware compiles for the ESP32-P4 and the board layer is written against M5Stack's published pinout, but no physical Tab5 has been connected. Nothing about it — boot, panel init, Wi-Fi via the ESP32-C6 co-processor, layout, touch navigation, OTA — has been observed working. `docs/TAB5_HARDWARE.md` lists exactly what to check first, in likelihood order. The Core2's own hardware testing is a good illustration of why a successful build proves very little.
- **Flight tracking has no schedule data, and can't see a flight before it departs.** ADS-B reports positions, not timetables: the ETA is a straight-line projection from current groundspeed, ignoring routing, holding and taxi time, and there is no "on time vs. delayed" because there is nothing to compare against. A flight whose transponder isn't on yet is indistinguishable from one that doesn't exist, so the display shows an explicit *waiting* state rather than guessing. Destination airports must be given as 4-letter ICAO (`KSEA`, not `SEA`) — the airport lookup returns nothing for IATA codes. The whole feature has been exercised against the live adsb.lol API and by unit tests, but **not yet end-to-end on hardware through an actual arrival**.
- **QR-code pairing is not the working path.** Scanning it with a phone's default camera app opens it as a dead link (now shows an explanatory page instead — see the fix — but doesn't complete pairing). The PWA's own in-app camera scanner cannot work at all in this system's normal deployment, because `navigator.mediaDevices` requires a secure (HTTPS) context, and this system runs over plain HTTP on the LAN by design (see `docs/ARCHITECTURE.md`). **Manual IP + code entry is the pairing method that actually works** and is the PWA's default.
- Two real crashes were found and fixed via hardware testing (a stack overflow in the ESP32's WebSocket handling, and an unhandled-promise-rejection crash in the gateway from a file-write race) — both are fixed and covered by regression tests where the underlying logic allows it, but a multi-day continuous-operation soak test hasn't been done, so a slower/rarer issue could still exist.
- A Wi-Fi-outage test (pulling the router's power to confirm the Core2 shows "Wi-Fi disconnected" and reconnects on its own) has not been performed — it would disrupt the tester's home network. The gateway-down/recovery equivalent *has* been tested and works correctly.
- No Playwright/browser-automation tool was available in this session, so there are no end-to-end browser tests yet (unit/component tests via Vitest + React Testing Library do exist and pass).
- The auto-reset sequence after flashing (`pio run -t upload`) did not reliably boot the specific unit/cable combination used for testing into normal run mode — it needed a manual power-cycle every time. See `docs/CORE2_HARDWARE.md`.
- Only Phase 1 features are implemented; see `docs/FEATURE_PARITY_MATRIX.md` for the full breakdown of what's done, planned, or future.

## Troubleshooting

There's no dedicated `docs/TROUBLESHOOTING.md` yet (planned for a later phase). For now: check the gateway's `/api/v1/status` endpoint, the Core2's own `/api/v1/status` endpoint, and the explicit status banner/screen shown on each display — every failure mode (Wi-Fi down, gateway unreachable, provider down, stale data, unconfigured) should be self-explanatory rather than a blank or spinning screen. If it isn't, that's a bug — please file an issue.

## Development setup

Each component has its own README with detailed setup: `firmware/display/README.md`, `apps/tablet-pwa/README.md`, `services/gateway/README.md`, `packages/*/README.md`.

## Contributing

See `CONTRIBUTING.md` — in particular, the licensing/attribution rules around the reference projects this design was informed by.

## License and attribution

MIT — see `LICENSE`. Reference-project licenses, aviation-data-provider terms, and map-data attribution: `docs/ATTRIBUTION.md`.
