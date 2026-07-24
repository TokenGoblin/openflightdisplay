# @openflightdisplay/protocol

Versioned wire protocol shared by `services/gateway` and `apps/tablet-pwa`: WebSocket message envelopes (`aircraft-update`, `heartbeat`, `provider-status`, `hello`) and REST request/response contracts (pairing, config, status).

Full human-readable spec: `docs/PROTOCOL.md`. Firmware (C++) mirrors this by hand in `firmware/core2/include/domain/protocol.h` — the two must be kept in sync, and `docs/PROTOCOL.md` is the contract of record when they disagree.

Every message carries `schemaVersion` (`CURRENT_SCHEMA_VERSION`, currently `1`). Bump it on any breaking shape change.

## Scripts

```
npm run build       # tsc -> dist/
npm run typecheck   # tsc --noEmit
npm test            # vitest run
```
