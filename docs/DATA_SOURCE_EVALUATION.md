# Aviation Data Source Evaluation

Evaluated 2026-07-24. This document is the source of truth for which providers OpenFlightDisplay supports, why, and with what caveats. Re-check terms before relying on any row marked "verify at implementation time" — provider terms and endpoints change without notice, and this table reflects a point-in-time review (some details, marked below, could not be fully verified via automated fetch in this session and need re-confirmation against current docs before shipping).

## Comparison

| Provider | Cost | Auth | Rate limit | Redistribution / commercial terms | Can ESP32 query directly? | Reliability | Chosen role |
|---|---|---|---|---|---|---|---|
| **Mock** (in-repo) | Free | None | N/A | N/A — synthetic data | N/A | Deterministic (this is the point) | Dev/test default, Phase 1 |
| **Replay** (recorded fixtures) | Free | None | N/A | Fixtures are sanitized, no real registration/PII beyond public ADS-B broadcast data | N/A | Deterministic | Dev/test, demos, Phase 1 |
| **adsb.lol** | Free | None currently required | Not fixed/published; described as dynamic based on load. **Verify at implementation time** — the project has signaled a future move toward requiring an API key. | ODbL 1.0 (open data) — hobby/community-friendly, arose as an open alternative after ADS-B Exchange's acquisition | No — HTTPS only, and TLS-on-ESP32 heap headroom is unverified without physical hardware, so we don't risk it in Phase 1 | Community-run, generally reliable, no SLA | **Phase 1 live provider**, via the gateway (never directly from firmware) |
| **airplanes.live** | Free | None | 1 request/second | ToS restricts to educational / non-commercial / personal use — fine for this project, but rules out any future paid/commercial fork using it as-is | No (same reasoning as adsb.lol) | Community-run, no SLA | Secondary adapter, implemented behind the same provider interface but not the Phase 1 default |
| **OpenSky Network** | Free (research-oriented) | Anonymous or OAuth2 client-credentials | 100 calls/day anonymous, 4,000/day authenticated (up to 8,000/day if you feed data back) | Requires attribution; commercial use (selling apps, ads, for-profit internal use beyond evaluation) is restricted | No | Academic/research project, good historical data, not built for high-frequency live polling | **Not used for live polling** — daily quota is too low for a continuously-refreshing home display. Documented as a future historical/enrichment source only |
| **ADS-B Exchange** | Community tier free (non-commercial); full API is a paid RapidAPI subscription (~$10/mo at time of review for 10k requests) | API key (RapidAPI) | Plan-dependent | Community API: non-commercial only. Paid tier terms reviewed separately at subscription time. | No | Commercial-grade | Optional Phase 4/5 paid adapter, not implemented in Phase 1 |
| **Local readsb / dump1090 / tar1090** | Free (requires the user's own SDR receiver) | None (LAN) | None (local) | No third-party terms — it's the user's own receiver | Yes, technically (plain HTTP, LAN-only, no TLS) — but not implemented in Phase 1 since no receiver was available to test against | Best possible reliability (no internet dependency, no rate limit) | **Recommended long-term path**, planned for Phase 4 |

## Why adsb.lol for Phase 1

- No API key currently required — lowest friction to get the vertical slice working end-to-end.
- Explicitly community/hobbyist-oriented, consistent with this project's non-commercial, local-first ethos.
- ODbL terms are compatible with "display live data with attribution," which is exactly what OpenFlightDisplay does — we are not redistributing a derived database.
- Being HTTPS-only pushed the architecture decision (see `docs/ARCHITECTURE.md`) to poll from the gateway, not the Core2 — which also means swapping providers later only touches one adapter file, not firmware.

## Why not put this decision on the ESP32

The Core2's ESP32 has a real but *unverified-on-this-hardware* TLS heap budget (see `docs/CORE2_HARDWARE.md`). Every candidate live provider is HTTPS-only. Rather than gamble on TLS stability on a device meant for continuous 24/7 operation, live-provider polling and normalization live in `services/gateway` (Node/TS), which talks plain, unencrypted WebSocket/HTTP to the Core2 over the LAN only. This also lets the gateway retry/backoff/cache without adding that complexity to firmware.

## Provider adapter interface (for implementers)

All adapters implement the same interface (see `services/gateway/src/providers/provider.ts`):

```ts
interface AviationDataProvider {
  readonly id: string;
  fetchAircraft(area: MonitoringArea): Promise<RawProviderAircraft[]>;
  readonly pollIntervalMs: number;
  readonly requiresApiKey: boolean;
}
```

Adding a new provider means: (1) read its current docs, auth, rate limits, and redistribution terms, (2) add a row to the table above, (3) implement the interface with its own backoff behavior, (4) add a fixture-backed test using a recorded (sanitized) sample response.

## Do-not-do list

- Never scrape a commercial website or an undocumented consumer endpoint as a production adapter.
- Never bundle a provider API key in firmware or in the PWA's client-side bundle. Keys belong in the gateway's environment/config only (see `docs/SECURITY_AND_PRIVACY.md`).
- Never claim schedule/ETA accuracy from a source that only provides positional ADS-B data (see `docs/PRODUCT_REQUIREMENTS.md`, Airport Mode).
