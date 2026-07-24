# OpenFlightDisplay Tablet PWA

React + Vite + TypeScript progressive web app: setup wizard, radar map, and flight-info card. See `docs/ARCHITECTURE.md`, `docs/PROVISIONING.md`, and `docs/PROTOCOL.md`. (A dedicated `docs/PWA_UI.md` is planned but not yet written -- out of scope for this Phase 1 session, see `docs/IMPLEMENTATION_PLAN.md`.)

## Setup

```
npm install                                        # from the repo root (npm workspaces)
npm run dev --workspace @openflightdisplay/tablet-pwa
```

Open the dev server URL on a tablet (or desktop browser) on the same LAN as your Core2/gateway.

## What it does in Phase 1

1. If no display has been paired yet, shows the setup wizard: pair (QR scan or manual IP/code entry) -> location (manual or browser geolocation, only requested on explicit user action) -> monitoring radius -> confirm.
2. Once paired, shows a basic map (Leaflet + OpenStreetMap raster tiles) and a flight-info card for the nearest aircraft, fed by the same gateway WebSocket the Core2 uses -- so both displays agree.
3. Persists only LAN connection info + a pairing token to `localStorage` -- never Wi-Fi credentials (those are entered once, directly on the Core2's own captive portal).

## Scripts

```
npm run dev
npm run build         # tsc --noEmit is a separate step: npm run typecheck
npm run typecheck
npm run test           # vitest run
```

## Known limitation for this session

Written and reviewed but not executed or built — no Node.js in this session's sandbox. Playwright end-to-end tests and real camera/WebSocket behavior are unverified; see `docs/TEST_PLAN.md`.
