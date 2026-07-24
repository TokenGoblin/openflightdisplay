# OpenFlightDisplay Gateway

Node.js + TypeScript + Fastify service that polls an aviation data provider, normalizes and ranks aircraft, and serves both the Core2 firmware and the tablet PWA over a shared local WebSocket feed. See `docs/ARCHITECTURE.md` for why this exists (short version: every live provider is HTTPS-only, and TLS on the ESP32 is a risk this project isn't taking without hardware to verify it on).

## Setup

```
npm install          # from the repo root (npm workspaces)
cp services/gateway/.env.example services/gateway/.env
npm run dev --workspace @openflightdisplay/gateway
```

The default `.env` uses `AVIATION_PROVIDER=mock`, so it runs with zero external dependencies out of the box.

## Scripts

```
npm run dev         # tsx watch — auto-restart on change
npm run build        # tsc -> dist/
npm run typecheck
npm run test          # vitest run
```

## Endpoints

See `openapi.yaml` for the full REST contract and `docs/PROTOCOL.md` for the WebSocket feed (`/ws/v1/aircraft`).

## Switching providers

Set `AVIATION_PROVIDER` in `.env` to `mock`, `replay`, or `adsblol`. See `docs/PROVIDER_ADAPTERS.md` before enabling `adsblol` against the real network — re-verify its current endpoint/rate-limit documentation first (flagged as unverified in this session, see `docs/DATA_SOURCE_EVALUATION.md`).

## Status

`npm test` passes (25/25 across 5 suites), `npm run typecheck` and `npm run build` are clean, and `npx eslint` reports no issues. Not covered: behavior against the real adsb.lol network (tests use the mock/replay providers and fixture data only) or multi-client load.
