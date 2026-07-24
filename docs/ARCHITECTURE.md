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
   ┌─────────────────────────┐        ┌────────────────────────────┐
   │  firmware/core2 (ESP32)  │        │  apps/tablet-pwa (React)    │
   │  - Wi-Fi provisioning    │◄──────►│  - setup wizard / pairing   │
   │  - pairing HTTP server   │  LAN   │  - map + flight card        │
   │  - single-aircraft render│ pairing│  - config editing           │
   │  - LittleFS config       │        │  - kiosk / full-screen modes│
   └─────────────────────────┘        └────────────────────────────┘
```

Four components, per the product brief:

1. **Core2 firmware** — glanceable display, optimized for 320×240, continuous operation.
2. **Tablet PWA** — setup/config surface, map/radar, flight board, and a standalone display mode that works without a Core2 present (as long as a gateway is reachable).
3. **Gateway** — the only component that talks HTTPS to the outside world for live aircraft data. Optional in the sense that a future phase could let advanced users point the Core2/PWA at a self-hosted `tar1090`/`dump1090` endpoint directly, but required for Phase 1's chosen provider (adsb.lol, HTTPS-only).
4. **Provider adapter layer** — inside the gateway; see `docs/PROVIDER_ADAPTERS.md` and `docs/DATA_SOURCE_EVALUATION.md`.

## Why the gateway is in the loop for Phase 1

The ESP32 could, in principle, speak TLS. But every viable live provider is HTTPS-only, and this project has no physical Core2 hardware to verify TLS heap headroom on (see `docs/CORE2_HARDWARE.md` — the memory budget there is explicitly marked estimated/unverified). Rather than ship firmware that might brownout or fragment heap on a device meant to run 24/7, provider polling, retry/backoff, and normalization all live in the gateway. The Core2 only ever speaks plain WebSocket/HTTP, and only on the LAN.

This also means: swapping or adding a provider touches one file in `services/gateway/src/providers/`, never firmware.

## Discovery and pairing flow

Browsers cannot browse mDNS services (there is no web-platform API for it), so the design does not depend on that:

1. **Core2 first boot**: no saved Wi-Fi credentials → starts a SoftAP (`OpenFlightDisplay-Setup-XXXX`) with a captive-portal HTTP server (ESPAsyncWebServer + DNSServer) serving a minimal Wi-Fi-credential form.
2. Core2 joins the home Wi-Fi, reboots into station mode, and starts:
   - an mDNS responder (`_openflightdisplay._tcp`) — a real capability on ESP32, even though browsers can't consume it directly;
   - its own small local HTTP server exposing pairing/config/status endpoints (see `docs/PROTOCOL.md`);
   - a full-screen QR code on its display encoding `http://<core2-ip>/pair?code=<6-digit>`, plus the IP and code as plain text (accessibility fallback, and for when camera access isn't available).
3. **Tablet PWA "Add Display"** step: scans the QR code (camera + `getUserMedia`, decoded client-side) **or** accepts manual IP + code entry. This is the realistic option for a browser — no OS/browser mDNS-browsing API exists to rely on instead.
4. PWA calls Core2's `/pair` endpoint with the code, receives a pairing token, and stores it (plus the Core2's LAN address) as the active device. The Wi-Fi password itself never touches the tablet — it was entered once, directly into the Core2's captive portal, over its temporary AP.
5. PWA writes location, monitoring radius, and the gateway URL to the Core2 via its local REST API (authenticated with the pairing token from step 4).
6. Core2 persists this configuration to LittleFS and opens a **WebSocket client** connection out to the configured gateway's `/ws/v1/aircraft` endpoint.
7. The PWA, independently, also opens a WebSocket connection to the same gateway endpoint for its own map/card view — guaranteeing "Core2 and tablet show the same aircraft," since both read from one source of truth.

## Data flow (steady state)

```
provider --(HTTPS poll)--> gateway --normalize--> AircraftState[]
                                   --rank/filter (per device's MonitoringArea+FilterProfile)-->
                                   --(WS push, bounded top-N, versioned envelope)--> Core2
                                   --(WS push, same or richer payload)--> tablet PWA
```

The gateway is the single ranking/filtering authority. Firmware still implements its own ranking/staleness logic (see `firmware/core2/src/domain/`) so that it can keep showing a sane last-known state if the gateway briefly drops a message, and so the logic is unit-testable without a network.

## Repository layout

See root `README.md` for the top-level tree; it follows the structure specified in the project brief (`firmware/`, `apps/`, `services/`, `packages/`, `datasets/`, `tools/`, `docs/`, `docker/`, `.github/workflows/`).

- `packages/shared-models` — versioned TypeScript + Zod schemas for every cross-cutting model (`AircraftState`, `MonitoringArea`, `FilterProfile`, `DisplayProfile`, `TrackedFlight`, `DeviceConfiguration`, `ProviderStatus`, `AlertRule`, `AircraftHistoryRecord`). Consumed by both `services/gateway` and `apps/tablet-pwa` so there is exactly one definition of each model in the TypeScript world.
- `packages/protocol` — the versioned wire format (WebSocket message envelopes + REST contract/OpenAPI) shared the same way.
- `firmware/core2` cannot depend on the TS packages (different language), so `docs/PROTOCOL.md` is the cross-language contract of record; firmware's C++ structs and the TS types in `packages/protocol` must be kept in sync by hand, and any drift is a protocol version bump.

## Resource-constrained design on the Core2

- Fixed maximum aircraft array size (bounded by the WS payload from the gateway, itself capped).
- `ArduinoJson` with a fixed-capacity `StaticJsonDocument`/filtered parsing — no unbounded String accumulation.
- Rendering is push-driven (redraw on WS message or a slow idle-clock tick), not polled in a busy loop.
- Config writes are atomic (write-temp-then-rename on LittleFS) so a power loss mid-write can't corrupt the active config; a corrupt/unreadable config falls back to a last-known-good copy or a "configuration required" screen — never a boot loop.

## What Phase 1 deliberately does NOT include

Multiple monitoring areas, polygon/cone areas, alerts, MQTT/Home Assistant, OTA, local dump1090 adapter, history/statistics, multi-device fleet management. These are real requirements from the product brief and are tracked in `docs/FEATURE_PARITY_MATRIX.md` and sequenced in `docs/IMPLEMENTATION_PLAN.md` — they are simply out of scope for the first vertical slice.
