# Architecture

## Components

```
                         ┌─────────────────────────┐
                         │   Aviation data provider │  (adsb.lol / airplanes.live /
                         │   (HTTPS, rate-limited)  │   OpenSky / ADS-B Exchange / mock / replay)
                         └────────────┬────────────┘
                                      │ HTTPS polling (server-side only)
                                      ▼
┌───────────────────────────────────────────────────────────────┐
│  services/gateway  (Node.js + TypeScript + Fastify)            │
│  - provider adapters -> normalized AircraftState[]             │
│  - ranking / filtering per device's MonitoringArea+FilterProfile│
│  - device config store (JSON file, Phase 1; SQLite in Phase 4) │
│  - REST: pairing, config CRUD, status                          │
│  - WebSocket: /ws/v1/aircraft (bounded push + heartbeat)        │
└───────────────┬───────────────────────────────┬────────────────┘
                │ plain WS/HTTP, LAN only        │ plain WS/HTTP, LAN or same-origin
                ▼                                ▼
   ┌──────────────────────────┐        ┌────────────────────────────┐
   │ firmware/display         │        │  apps/tablet-pwa (React)    │
   │  (Core2 / Tab5)          │        │                             │
   │  - Wi-Fi provisioning    │◄──────►│  - setup wizard / pairing   │
   │  - pairing HTTP server   │  LAN   │  - map + flight card        │
   │  - FIDS render           │ pairing│  - config editing           │
   │  - LittleFS config       │        │  - kiosk / full-screen modes│
   └──────────────────────────┘        └────────────────────────────┘
```

Four components, per the product brief:

1. **Device firmware** — glanceable display, continuous operation. One source tree builds for both supported boards; a compile-time board layer isolates the panel geometry, the Wi-Fi bring-up and the navigation input, and nothing above it is board-aware. See `docs/DISPLAY_UI.md`.
2. **Tablet PWA** — setup/config surface, map/radar, flight board, and a standalone display mode that works without a device present (as long as a gateway is reachable).
3. **Gateway** — the only component that talks HTTPS to the outside world for live aircraft data. Optional in the sense that a future phase could let advanced users point the Core2/PWA at a self-hosted `tar1090`/`dump1090` endpoint directly, but required for Phase 1's chosen provider (adsb.lol, HTTPS-only).
4. **Provider adapter layer** — inside the gateway; see `docs/PROVIDER_ADAPTERS.md` and `docs/DATA_SOURCE_EVALUATION.md`.

## Where provider polling actually happens

> **This section previously said the opposite,** and was wrong for long enough to be worth recording rather than quietly overwriting. It claimed provider polling "all live[s] in the gateway" and that "the Core2 only ever speaks plain WebSocket/HTTP, and only on the LAN" — on the reasoning that no physical hardware existed to verify TLS heap headroom. Both premises expired: a Core2 was acquired and tested end to end, and the firmware was changed to poll adsb.lol over HTTPS directly. The document wasn't updated, so it spent several commits describing an architecture the code had already left.

**The device polls the provider directly, over HTTPS.** `firmware/display/src/app/adsb_provider.cpp` calls `https://api.adsb.lol/...` from a dedicated FreeRTOS task — three endpoints: the geographic query for nearest aircraft, a callsign lookup for a tracked flight, and an airport lookup to resolve a destination.

The original concern — mbedTLS heap on a PSRAM-less ESP32 — turned out to be manageable, but only because the buffers are bounded deliberately. See `docs/CORE2_HARDWARE.md`'s memory budget: a fixed parse buffer, a field filter that discards ~70% of each response before it reaches memory, streaming parse so the payload is never materialised as a String, and a clamped query radius. Those are not incidental; they are what makes TLS on this board safe, and an audit found the display would silently and permanently blank when the last of them was missing.

**The gateway is therefore optional for a single device.** It still exists and still earns its place for: multiple displays sharing one upstream poll (rather than each device hitting the provider independently), the tablet PWA's own feed, provider adapters that need credentials you'd rather not put on a device, and future history/alerting that needs somewhere to persist. A device with no gateway on the network works fine; a fleet of them without one is rude to a free, community-funded data source.

Swapping providers is consequently *not* a one-file change any more: it touches `services/gateway/src/providers/` **and** the firmware's own poller. That's a real cost of the direct-polling design and should be weighed if a second provider is ever added.

## Discovery and pairing flow

Browsers cannot browse mDNS services (there is no web-platform API for it), so the design does not depend on that:

1. **Core2 first boot**: no saved Wi-Fi credentials → starts a SoftAP (`OFD-Setup-XXXXXX`) with a captive-portal HTTP server (ESPAsyncWebServer + DNSServer) serving a minimal Wi-Fi-credential form (with a network scan/picker, `GET /wifi-scan`).
2. Core2 joins the home Wi-Fi, reboots into station mode, and starts:
   - an mDNS responder (`_openflightdisplay._tcp`) — a real capability on ESP32, even though browsers can't consume it directly;
   - its own small local HTTP server exposing pairing/config/status endpoints (see `docs/PROTOCOL.md`);
   - a full-screen QR code on its display encoding `http://<core2-ip>/pair?code=<6-digit>`, plus the IP and code as plain text.
3. **Tablet PWA "Add Display"** step: accepts manual IP + code entry as the default and primary path, or scanning the QR code as a secondary option. **Confirmed via real hardware testing that QR scanning does not actually work in this system's normal deployment**: a phone's default camera app just opens the encoded URL as a plain link (the Core2 now serves an explanatory page there instead of a dead 404, but it still doesn't complete pairing by itself), and the PWA's own in-app camera scanner (`getUserMedia`) cannot run at all, because `navigator.mediaDevices` requires a secure (HTTPS) context and this system is plain HTTP on the LAN by design (see "Why the gateway is in the loop" above). Manual entry is not a fallback here — it is the only pairing path that actually works today. A future phase could revisit this (e.g., serving the pairing page over HTTPS with a self-signed/local CA), but that's out of scope for Phase 1.
4. PWA calls Core2's `/pair` endpoint with the code, receives a pairing token, and stores it (plus the Core2's LAN address) as the active device. The Wi-Fi password itself never touches the tablet — it was entered once, directly into the Core2's captive portal, over its temporary AP.
5. PWA writes location, monitoring radius, and the gateway URL to the Core2 via its local REST API (authenticated with the pairing token from step 4).
6. The device persists this configuration to LittleFS and starts its own **HTTPS poller** against adsb.lol (see "Where provider polling actually happens" above). It does *not* open a WebSocket to the gateway; that step existed in an earlier design and the gateway-client code has since been removed from the firmware entirely.
7. The PWA, independently, also opens a WebSocket connection to the same gateway endpoint for its own map/card view — guaranteeing "Core2 and tablet show the same aircraft," since both read from one source of truth.

## Data flow (steady state)

```
                    ┌─(HTTPS poll, filtered+streamed)─► firmware/display
provider (adsb.lol) ┤                                    normalize → rank → render
                    └─(HTTPS poll)─► gateway ─(WS push)─► tablet PWA
```

**Two independent paths to the same provider**, which is the honest picture rather than one pipeline. The device normalizes and ranks its own responses (`firmware/display/src/domain/` — `parseAdsbAircraft`, `rankNearest`, staleness); the gateway does the same for the PWA in TypeScript (`services/gateway/src/`).

That duplication is deliberate but not free. `domain/geo.h` says outright that it "mirrors `services/gateway/src/lib/geo.ts` exactly" so the two agree, and `docs/PROTOCOL.md` is the cross-language contract of record. The benefit is that a device keeps working with no gateway on the network at all, and the ranking logic stays unit-testable without one. The cost is two implementations to keep in step — see "Where provider polling actually happens" for when that cost is worth paying.

## Repository layout

See root `README.md` for the top-level tree; it follows the structure specified in the project brief (`firmware/`, `apps/`, `services/`, `packages/`, `datasets/`, `tools/`, `docs/`, `docker/`, `.github/workflows/`).

- `packages/shared-models` — versioned TypeScript + Zod schemas for every cross-cutting model (`AircraftState`, `MonitoringArea`, `FilterProfile`, `DisplayProfile`, `TrackedFlight`, `DeviceConfiguration`, `ProviderStatus`, `AlertRule`, `AircraftHistoryRecord`). Consumed by both `services/gateway` and `apps/tablet-pwa` so there is exactly one definition of each model in the TypeScript world.
- `packages/protocol` — the versioned wire format (WebSocket message envelopes + REST contract/OpenAPI) shared the same way.
- `firmware/display` cannot depend on the TS packages (different language), so `docs/PROTOCOL.md` is the cross-language contract of record; firmware's C++ structs and the TS types in `packages/protocol` must be kept in sync by hand, and any drift is a protocol version bump.

## Resource-constrained design on the device

These constraints are written against the tightest supported board (the Core2: no PSRAM, ~4.5MB addressable static RAM budget) and are applied on every board rather than tuned per board. The Tab5 has 32MB of PSRAM and could afford to be looser, but a bounded aircraft array and a fixed-capacity JSON document are correctness properties, not just memory savings — a payload that can't overflow behaves the same everywhere. The one place the boards genuinely diverge is the display buffer, which is a `board::kUseFullScreenCanvas` trait; see `docs/DISPLAY_UI.md`'s "Sprite and buffering strategy".


- Fixed maximum aircraft array size (bounded by the WS payload from the gateway, itself capped).
- `ArduinoJson` with a fixed-capacity `StaticJsonDocument`/filtered parsing — no unbounded String accumulation.
- Rendering is push-driven (redraw on WS message or a slow idle-clock tick), not polled in a busy loop.
- Config writes are atomic (write-temp-then-rename on LittleFS) so a power loss mid-write can't corrupt the active config; a corrupt/unreadable config falls back to a last-known-good copy or a "configuration required" screen — never a boot loop.

## What Phase 1 deliberately does NOT include

Multiple monitoring areas, polygon/cone areas, alerts, MQTT/Home Assistant, OTA, local dump1090 adapter, history/statistics, multi-device fleet management. These are real requirements from the product brief and are tracked in `docs/FEATURE_PARITY_MATRIX.md` and sequenced in `docs/IMPLEMENTATION_PLAN.md` — they are simply out of scope for the first vertical slice.
