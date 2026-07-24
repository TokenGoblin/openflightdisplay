# Security and Privacy

## Data handled

| Data | Where it lives | Sensitivity |
|---|---|---|
| Wi-Fi SSID/password | Entered once into the Core2's captive portal, stored only on the Core2 (LittleFS), never sent to the gateway or PWA | High |
| Approximate home/monitoring location | Core2 LittleFS + gateway config store; optionally the PWA's `localStorage` for wizard convenience | High (physical location) |
| Pairing token | Core2 LittleFS + PWA `localStorage`/session | Medium — grants config-write access to one Core2 on the LAN |
| Provider API keys (future providers that need them) | Gateway environment/config only | High — must never reach firmware or the PWA client bundle |
| Aircraft position data | Transient, in-memory on gateway; not persisted in Phase 1 (history is Phase 4, opt-in) | Public broadcast data (ADS-B), not personal data |

## Principles (from the product brief, all binding for Phase 1)

- **Local-first.** No mandatory account. The gateway runs on the user's own LAN/hardware.
- **No telemetry by default.** No analytics SDKs, no ad SDKs, no third-party telemetry anywhere in firmware, gateway, or PWA.
- **Explicit permission before geolocation.** The PWA only requests browser geolocation when the user takes the "use my location" action, never on load.
- **Location never leaves the LAN.** The PWA does not send location to any third-party analytics service. It **does** implicitly reach a third-party aviation-data provider (adsb.lol by default) via the gateway, because that's how "aircraft near me" queries work — this is disclosed in the PWA's setup flow and in `docs/DATA_SOURCE_EVALUATION.md`, not hidden.
- **No Wi-Fi credentials leave the Core2.** The tablet never stores or transmits the Wi-Fi password; it's typed directly into the Core2's own temporary access point.
- **Secrets never in firmware.** Provider API keys (when a provider needs one) live only in the gateway's environment.

## Threat model (Phase 1 scope)

In scope:
- A malicious device on the same LAN attempting to read or write Core2/gateway configuration without pairing.
- A malformed/oversized message from the gateway or an untrusted network peer crashing the Core2 (bounded parsing, input validation).
- A compromised or malicious aviation-data-provider response causing a crash or resource exhaustion in the gateway (bounded parsing, schema validation on ingest).
- Accidental secret leakage into logs.

Explicitly out of scope for Phase 1 (documented, not solved yet):
- A fully adversarial local network (no WPA-Enterprise-grade device isolation) — Phase 1 assumes a typical home LAN where the Wi-Fi network itself is the trust boundary.
- Protection against a user who already has physical access to the Core2 (e.g., re-flashing it) — that's expected, it's the user's own hardware.
- Multi-tenant/fleet security model (Phase 5).

## Controls implemented in Phase 1

- **Pairing token required for configuration writes.** After initial captive-portal Wi-Fi setup, the Core2's `/api/v1/config` endpoints reject writes without a valid pairing token (see `docs/PROTOCOL.md`).
- **Input validation at every boundary.** Gateway REST/WS payloads are validated against the Zod schemas in `packages/shared-models`/`packages/protocol` before use. Firmware JSON parsing uses bounded `ArduinoJson` documents and rejects/ignores malformed or oversized messages rather than crashing.
- **Rate-limiting on provisioning/admin endpoints.** The gateway's pairing/config endpoints apply a basic rate limit to blunt brute-forcing of pairing codes.
- **Secret redaction in logs.** The gateway's structured logger redacts known secret-shaped fields (API keys, pairing tokens, Wi-Fi passwords if ever logged accidentally) before writing log lines.
- **No secrets in the repo.** `.env` is gitignored; `.env.example` documents required variables without values.
- **TLS acknowledgment.** LAN traffic between Core2/PWA/gateway is plaintext HTTP/WS in Phase 1 (typical for ESP32-provisioning-constrained local devices, and consistent with how similar open-source projects operate). This is a documented, accepted trade-off for a LAN-only protocol, not an oversight — captured here so it's revisited if the threat model changes (e.g., a future "expose gateway to the internet" feature would require TLS termination in front of it, out of scope for Phase 1).

## Dependency update policy

- Gateway and PWA dependencies (`npm`) should be reviewed for known-vulnerability advisories before each release; `.github/workflows` includes a dependency-scanning step (see `docs/TEST_PLAN.md`).
- Firmware library versions are pinned in `platformio.ini`; bumping them is a deliberate, reviewed change, not automatic, since a library update on firmware can't be trivially rolled back without physical access pre-OTA.

## Security reporting

See root `SECURITY.md`.
