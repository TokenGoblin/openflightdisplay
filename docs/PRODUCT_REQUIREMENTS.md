# Product Requirements

## Product goal

OpenFlightDisplay provides functional parity with the general "home flight-tracking display" product category — not a pixel-for-pixel clone of any specific commercial product. It runs on an M5Stack Core2 (compact always-on display) and a tablet PWA (setup, radar/map, flight board, optional standalone display).

## Target users

- Aviation enthusiasts who want an ambient display of nearby air traffic.
- Homes near an airport or under a flight path who want a friendly indicator of what they're hearing/seeing overhead.
- Hobbyists who already run (or want to run) their own ADS-B receiver and want a local, private display for it.

## User capabilities (from the product brief)

The user should be able to:

- See aircraft currently flying near a selected location.
- Track a particular flight, registration, or ICAO hex address (Phase 3).
- Define circular (Phase 1), directional cone, and polygon (Phase 3) monitoring areas.
- Filter aircraft by useful criteria (Phase 2+, see Feature Parity Matrix).
- Select what information appears on each display (Phase 2+).
- Use the Core2 as a compact countertop/dashboard display (Phase 1 minimal version, expanded Phase 2+).
- Use the tablet as a large flight board, radar display, or hybrid layout (Phase 1 basic map, expanded Phase 3).
- Configure one or more Core2 devices from the tablet (single device in Phase 1; fleet in Phase 5).
- Use local ADS-B data when available (Phase 4).
- Optionally use an external aviation-data provider (Phase 1 — this is the default path).
- Retain basic functionality during temporary internet failures (Phase 1: explicit stale/error states, last-known-good aircraft state cached).
- Understand system state, data age, and errors at a glance (Phase 1, non-negotiable — no indefinite loading states anywhere).

## Non-goals (explicit)

- Pixel-identical recreation of any commercial product's visual design, branding, or copy.
- Guaranteed schedule/ETA accuracy — the system is honest that ADS-B position data and schedule/enrichment data are different kinds of information and labels them as such.
- Support for detecting or evading any specific commercial product's telemetry/DRM — not applicable, we're not interoperating with one.
- Cloud accounts, telemetry, or ads. This is a local-first, no-account system by design (see `docs/SECURITY_AND_PRIVACY.md`).

## Phase 1 vertical-slice acceptance criteria

These are the concrete, testable statements that define "Phase 1 works":

1. Core2 boots, and if no Wi-Fi is configured, starts a SoftAP + captive portal for credential entry; once configured, it connects automatically on every subsequent boot.
2. A tablet can open the PWA (installed or in-browser) and start an "Add Display" flow.
3. The PWA can pair with a Core2 by scanning its QR code or entering its IP + pairing code manually.
4. The user can enter a location (manual lat/lon or "use my current location" with an explicit permission prompt) and a monitoring radius, and this is saved to both the Core2 and the gateway.
5. The gateway fetches live aircraft data from a configurable provider (adsb.lol by default in Phase 1; mock/replay always available) on a bounded polling interval.
6. The Core2 displays the single nearest aircraft within the configured area, with distance/bearing/altitude/callsign, and an explicit "no matching aircraft" state when there is none.
7. The tablet shows the same nearest aircraft on a basic map plus an info card, fed from the same gateway feed as the Core2.
8. All configuration (Wi-Fi, pairing token, location, radius, gateway URL) survives a Core2 reboot and a gateway restart.
9. Wi-Fi loss, gateway unreachability, and provider failures each produce a specific, human-readable status message on both Core2 and PWA — never a crash, never an indefinite spinner.

## Units and localization (Phase 1 scope)

Phase 1 ships with a single fixed unit set (metric: km, km/h, meters) and English-only copy. Configurable units (imperial/aviation) and localization are tracked for Phase 2+ in `docs/FEATURE_PARITY_MATRIX.md` — the data model already carries raw SI-ish values so unit conversion is a presentation-layer concern, not a data-model migration, when it's implemented.

## Definition of done (per feature, applies from Phase 1 onward)

A feature is complete only when: behavior is documented, implementation is functional, configuration is validated, errors are handled, tests exist, Core2 resource impact is considered, tablet behavior is responsive, protocol changes are versioned, privacy implications are considered, docs are updated, and `docs/FEATURE_PARITY_MATRIX.md` status is updated.
