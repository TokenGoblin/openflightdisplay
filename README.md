# OpenFlightDisplay

An open-source aircraft-tracking display system for the M5Stack Core2 and a tablet Progressive Web App — see nearby air traffic at a glance, on a compact always-on display and/or a larger tablet radar/flight-board view.

OpenFlightDisplay is an **original implementation**, inspired by the general "home flight-tracking display" product category (products like TheFlightWall) but not a fork, clone, or port of any commercial product's source code, branding, or visual design. See `docs/ATTRIBUTION.md` for exactly what was studied, under what license, and what that does and doesn't permit.

> **Status: Phase 1 vertical slice.** This is a narrow, end-to-end working slice (Wi-Fi provisioning, pairing, one live data provider, single-aircraft display) — not the full feature set described in `docs/PRODUCT_REQUIREMENTS.md`. See `docs/FEATURE_PARITY_MATRIX.md` for what's done vs. planned, and `docs/IMPLEMENTATION_PLAN.md` for the phased roadmap.
>
> **This code was written in a sandbox with no Node.js, PlatformIO, or C++ compiler available.** Everything below was written and carefully reviewed but not executed. See "Known limitations" below and `docs/TEST_PLAN.md` before trusting any of it.

## Product overview

- **Core2**: a compact, always-on countertop display showing the nearest aircraft to a configured location, with explicit status states (never an indefinite spinner) for every failure mode.
- **Tablet PWA**: the setup/configuration surface (Wi-Fi-free pairing via QR code, location + radius picker), a basic radar map + flight-info card, and — once a gateway is reachable on the LAN — a display mode that doesn't require the Core2 at all.
- **Gateway**: a small local service that polls a live aviation-data provider, normalizes and ranks aircraft, and serves both displays over a shared local WebSocket feed.

No screenshots yet — there's no physical hardware or a UI review pass in this session; see "Known limitations."

## Supported hardware

- **M5Stack Core2** (ESP32, 320×240 capacitive touchscreen). See `docs/CORE2_HARDWARE.md` for the memory budget and what's verified vs. estimated.
- Any modern tablet/phone/desktop browser for the PWA (installable; works standalone without a Core2 as long as a gateway is reachable).
- Any small Linux machine / Raspberry Pi / Docker host / plain laptop to run the gateway.

## Architecture

```
provider (adsb.lol / mock / replay)
        │ HTTPS polling (gateway only — never the ESP32; see docs/ARCHITECTURE.md)
        ▼
services/gateway  ──WS/HTTP (LAN, plaintext)──►  firmware/core2 (Core2 display)
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

### 3. Core2 firmware

```
cd firmware/core2
pio test -e native          # domain-logic unit tests, no hardware needed
pio run -e core2            # build for the real board
pio run -e core2 -t upload -t monitor   # flash + serial monitor (needs a connected Core2)
```

See `firmware/core2/README.md` and `docs/PROVISIONING.md` for the Wi-Fi setup / pairing flow.

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

- **Nothing in this repository has been compiled, installed, or run.** The session that wrote it had `git` but no Node.js, npm, PlatformIO, or C++ compiler. Run the test suites yourself (see `docs/TEST_PLAN.md`) before trusting any component.
- The `adsb.lol` adapter's exact endpoint path is a best-effort implementation based on the common tar1090-derived API convention — re-verify against `https://api.adsb.lol/docs` before relying on it.
- Firmware library versions in `firmware/core2/platformio.ini` are believed current but unverified against the PlatformIO registry.
- Only Phase 1 features are implemented; see `docs/FEATURE_PARITY_MATRIX.md` for the full breakdown of what's done, planned, or future.
- No physical M5Stack Core2 was available — every hardware-specific claim in `docs/CORE2_HARDWARE.md` is marked as estimated or unverified.

## Troubleshooting

There's no dedicated `docs/TROUBLESHOOTING.md` yet (planned for a later phase). For now: check the gateway's `/api/v1/status` endpoint, the Core2's own `/api/v1/status` endpoint, and the explicit status banner/screen shown on each display — every failure mode (Wi-Fi down, gateway unreachable, provider down, stale data, unconfigured) should be self-explanatory rather than a blank or spinning screen. If it isn't, that's a bug — please file an issue.

## Development setup

Each component has its own README with detailed setup: `firmware/core2/README.md`, `apps/tablet-pwa/README.md`, `services/gateway/README.md`, `packages/*/README.md`.

## Contributing

See `CONTRIBUTING.md` — in particular, the licensing/attribution rules around the reference projects this design was informed by.

## License and attribution

MIT — see `LICENSE`. Reference-project licenses, aviation-data-provider terms, and map-data attribution: `docs/ATTRIBUTION.md`.
