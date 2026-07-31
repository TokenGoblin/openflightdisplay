# Protocol: Gateway ↔ Core2 ↔ Tablet PWA

All messages — REST bodies and WebSocket frames alike — are JSON and carry an explicit `schemaVersion` (currently `1`). A breaking change to any payload shape bumps this number; the receiver must reject (not guess-parse) a `schemaVersion` it doesn't understand and surface a clear "unsupported protocol version" status rather than crashing or silently misbehaving.

Canonical TypeScript definitions live in `packages/protocol/src`. Firmware's C++ structs in `firmware/display/include/domain/protocol.h` are a hand-maintained mirror — any change to one must be reflected in the other and in this document. This is the cross-language contract of record.

## Transport summary

| Link | Transport | Auth | TLS |
|---|---|---|---|
| PWA ↔ gateway | REST (config/pairing) + WebSocket (`/ws/v1/aircraft`) | Pairing token (per device) for config writes | Plaintext, LAN-only (see `docs/SECURITY_AND_PRIVACY.md`) |
| PWA ↔ device | REST (pairing claim, config CRUD, status) served by the device itself | Pairing token (except the initial `/pair` claim, which consumes a short-lived pairing code) | Plaintext, LAN-only |
| device → provider | HTTPS GET (adsb.lol) | None required by the provider | **TLS**, outbound to the internet |
| ~~device ↔ gateway~~ | ~~WebSocket client~~ | — | **Not implemented.** The firmware polls the provider directly; the gateway-client code was removed. The message schemas below are retained as the contract for the PWA's feed and for any future gateway-mediated mode |

## Core2's own local HTTP API (served by the Core2)

`POST /pair`
```json
// request
{ "schemaVersion": 1, "code": "482913" }
// response (200)
{ "schemaVersion": 1, "pairingToken": "<opaque>", "deviceId": "core2-xxxxxx" }
// response (401) if code is wrong or expired
{ "schemaVersion": 1, "error": "invalid_or_expired_code" }
```
The pairing code is generated fresh on every boot where the device is unpaired, is 6 digits, and expires after 10 minutes or on the first successful claim (single-use).

`GET /api/v1/status` (no auth required — status is not sensitive)
```json
{
  "schemaVersion": 1,
  "deviceId": "core2-xxxxxx",
  "firmwareVersion": "0.1.0",
  "wifiState": "connected" | "disconnected" | "provisioning",
  "gatewayConnectionState": "connected" | "connecting" | "disconnected" | "unconfigured",
  "lastAircraftUpdateAgeSeconds": 4,
  "freeHeapBytes": 123456
}
```

`GET /api/v1/config` / `PUT /api/v1/config` (requires `Authorization: Bearer <pairingToken>`)
```json
{
  "schemaVersion": 1,
  "deviceName": "Living Room",
  "gatewayUrl": "ws://192.168.1.50:8787/ws/v1/aircraft",
  "monitoringArea": { "kind": "circle", "centerLat": 47.6, "centerLon": -122.3, "radiusKm": 15 },
  "trackedFlight": { "flight": "UA1234", "callsign": "UAL1234", "destinationIcao": "KSEA" },
  "displayProfile": { "mode": "single-aircraft", "brightness": 200 }
}
```
`PUT` validates the full body against the shared schema before writing; a partial/invalid body is rejected with `400` and the previous config is left untouched (no partial writes).

### `trackedFlight`

Follows one flight to its destination. **Three distinct states, and the difference is binding:**

| Value | Meaning |
|---|---|
| key absent | Leave existing tracking untouched — a `PUT` changing only `brightness` must not cancel someone's airport run |
| `null` | Stop tracking |
| object | Start tracking |

- `flight` — what the user typed: a boarding-pass flight number (`"UA1234"`) or a raw ADS-B callsign (`"UAL1234"`). **The device performs the IATA→ICAO translation**, because it already carries the airline table for decoding callsigns; a second table in TypeScript would be a second source of truth that could silently disagree. Rejected with `400` if it contains no digits.
- `callsign` — the normalized result (`"UAL1234"`), the identifier actually queried against ADS-B. Device-derived: **returned by `GET`, ignored by `PUT`.**
- `destinationIcao` — the arrival airport, **4-letter ICAO only** (`"KSEA"`, not `"SEA"`). ADS-B carries no destination, so the user supplies it; the airport lookup that resolves it to coordinates answers `null` for IATA codes, and expanding `"SEA"` to `"KSEA"` is a North-America-only assumption. Rejected with `400` rather than guessed at.

The device exposes the resulting live state (phase, ETA, distance) through `GET /api/v1/status` — it is derived, never configured.

## Gateway's REST API

`POST /api/v1/devices/:deviceId/claim` — PWA-initiated pairing completion mirror (so the PWA can register the same device with the gateway after pairing with the Core2 directly); body/response shapes mirror the Core2's `/pair` above plus a `deviceName`.

`GET /api/v1/devices/:deviceId/config`, `PUT /api/v1/devices/:deviceId/config` — same `DeviceConfiguration` shape as above; this is the gateway's own copy (used to know which `MonitoringArea`/`FilterProfile` to rank/filter for that device's WS stream), kept in sync with the Core2's copy by the PWA writing to both.

`GET /api/v1/status` — gateway + active provider status:
```json
{
  "schemaVersion": 1,
  "provider": { "id": "adsblol", "status": "ok" | "degraded" | "unavailable", "lastSuccessfulPollAt": "2026-07-24T12:00:00Z" },
  "connectedDevices": 2
}
```

Full request/response schemas are also captured as an OpenAPI 3.0 document at `services/gateway/openapi.yaml`.

## WebSocket: `/ws/v1/aircraft?deviceId=<id>&token=<pairingToken>`

Server → client messages (all carry `schemaVersion` and a `type` discriminator):

```json
{ "schemaVersion": 1, "type": "aircraft-update", "aircraft": [ /* AircraftState[], bounded to top N */ ], "generatedAt": "2026-07-24T12:00:00.000Z" }
```
```json
{ "schemaVersion": 1, "type": "heartbeat", "serverTime": "2026-07-24T12:00:05.000Z" }
```
```json
{ "schemaVersion": 1, "type": "provider-status", "provider": "adsblol", "status": "ok" | "degraded" | "unavailable", "message": "adsb.lol unreachable, retrying" }
```

Client → server (firmware displays and the PWA both send these):
```json
{ "schemaVersion": 1, "type": "hello", "deviceId": "core2-xxxxxx", "role": "core2" | "tab5" | "pwa" }
```

`role` identifies the kind of client. Firmware displays announce their board (`core2`, `tab5`) rather than a single generic `display` value, and `deviceId` carries the same board prefix. Adding a supported board means adding a value here, to `packages/protocol/src/wsMessages.ts`, and to `ofd::board::kDeviceIdPrefix` in `firmware/display/include/board/board.h`.

## Bounds and reliability rules (binding for every implementation)

- `aircraft-update.aircraft` is capped at a fixed maximum length (Phase 1: 10) regardless of how many the provider returns — the gateway ranks/truncates before sending, never the receiver.
- Heartbeats are sent every 15s; a client that receives no message (heartbeat or otherwise) for 45s treats the connection as dead and reconnects with exponential backoff + jitter (base 1s, cap 30s).
- Every message is size-bounded on the receiving side (firmware uses a fixed-capacity `ArduinoJson` document and drops/logs oversized frames rather than allocating unbounded memory).
- A `provider-status` of `unavailable` does not clear the last-known aircraft list on the receiver — it's rendered alongside a visible "data source unavailable, showing data from Xs ago" indicator, per the no-silent-failure requirement in `docs/PRODUCT_REQUIREMENTS.md`.

## Versioning policy

`schemaVersion` bumps on any breaking change to a message shape (field removed/retyped/required-added). Additive, optional fields do not require a bump. Both gateway and firmware log and gracefully reject an unrecognized higher `schemaVersion` rather than attempting best-effort parsing.
