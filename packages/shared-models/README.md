# @openflightdisplay/shared-models

Versioned TypeScript + [Zod](https://zod.dev) schemas for every cross-cutting OpenFlightDisplay model: `AircraftState`, `MonitoringArea`, `FilterProfile`, `DisplayProfile`, `TrackedFlight`, `DeviceConfiguration`, `ProviderStatus`, `AlertRule`, `AircraftHistoryRecord`.

Consumed by `services/gateway` and `apps/tablet-pwa` so there is exactly one definition of each model in the TypeScript world. Firmware (C++) mirrors these by hand in `firmware/core2/include/domain/protocol.h` — see `docs/PROTOCOL.md` for the cross-language contract of record.

## Usage

```ts
import { AircraftStateSchema, type AircraftState } from "@openflightdisplay/shared-models";

const aircraft: AircraftState = AircraftStateSchema.parse(rawJson);
```

## Scripts

```
npm run build       # tsc -> dist/
npm run typecheck   # tsc --noEmit
npm test            # vitest run
```
