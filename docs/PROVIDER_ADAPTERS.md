# Provider Adapters

Implementation guide for `services/gateway/src/providers/`. See `docs/DATA_SOURCE_EVALUATION.md` for the evaluation behind these choices and `docs/ARCHITECTURE.md` for why polling happens in the gateway, not on the Core2.

## Interface

```ts
// services/gateway/src/providers/provider.ts
export interface AviationDataProvider {
  readonly id: string;
  readonly requiresApiKey: boolean;
  readonly pollIntervalMs: number;
  fetchAircraft(area: MonitoringArea): Promise<RawProviderAircraft[]>;
}
```

Each adapter:
1. Owns its own request shape and auth (API key from `process.env`, never hardcoded, never exposed to the PWA).
2. Returns provider-native shapes (`RawProviderAircraft`, adapter-specific) — normalization to `AircraftState` happens in one shared function (`normalizeAircraft`) so ranking/filtering code never has to know which provider produced a record.
3. Implements its own backoff on failure (exponential, capped) and reports status via the shared `ProviderStatus` model rather than throwing uncaught.
4. Never logs its API key or full request URL if the key is embedded in the URL (redact before logging).

## Phase 1 adapters

### `mock`
Synthetic data generator with no network dependency. Produces a small number of plausible aircraft around the requested `MonitoringArea`'s center, moving deterministically frame-to-frame. Used as the default in local development and in every unit test that doesn't specifically test a real adapter.

### `replay`
Reads a JSON fixture file (see `services/gateway/tests/fixtures/`) and plays it back on a timer, looping. Used for deterministic demos and tests that need a specific scenario (e.g., "provider outage," "aircraft with stale position").

### `adsblol`
Calls `https://api.adsb.lol` for aircraft near the `MonitoringArea`'s center within its radius. No API key currently required (this was re-verified live, and terms can still change over time — re-check `https://api.adsb.lol/docs` periodically; see `docs/DATA_SOURCE_EVALUATION.md`). Polls no faster than once every 15 seconds by default (configurable, conservative default since the provider's rate limit is described as dynamic/load-based rather than a fixed published number).

**Confirmed working end-to-end** against a real M5Stack Core2 and real air traffic: switching `AVIATION_PROVIDER=adsblol` in `.env` and setting a real monitoring area showed live, moving commercial flights (multiple different real flights observed over time, e.g. a Delta 737 and American/SkyWest regional jets) on both the Core2's display and the tablet PWA.

## Documented but not implemented in Phase 1

- `airplanes.live` — same shape as `adsblol`, 1 req/sec hard cap, non-commercial ToS. Straightforward to add behind the same interface; not wired up by default.
- `opensky` — documented as unsuitable for live polling (100-4000 req/day quota); would need a very long poll interval and is better suited to a future historical/enrichment use case.
- `adsbexchange` — requires a paid RapidAPI key; adapter would read the key from `process.env.ADSBEXCHANGE_API_KEY` and never expose it beyond the gateway process.
- `dump1090` / `tar1090` (local receiver) — plain HTTP, LAN-only, no key. Best long-term option; deferred to Phase 4 because no receiver was available to test against in this session.

## Adding a new provider (checklist)

1. Read its current docs, auth model, rate limits, and redistribution/commercial terms.
2. Add a row to `docs/DATA_SOURCE_EVALUATION.md`.
3. Implement `AviationDataProvider` in its own file; add a fixture-backed normalization test using a recorded (sanitized) sample response — never test against the live network.
4. If it needs a key, document the required environment variable in `services/gateway/.env.example` and never in code.
5. Update `docs/FEATURE_PARITY_MATRIX.md`'s "Data-source dependency" column for any feature that now becomes possible/more reliable.
