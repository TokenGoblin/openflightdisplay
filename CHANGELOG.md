# Changelog

Notable changes to OpenFlightDisplay. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Nothing has been released yet — the project is a Phase 1 vertical slice
(see `docs/IMPLEMENTATION_PLAN.md`), so everything below sits under
Unreleased. Entries record what changed **and what remains unverified**,
because on this project the gap between "it compiles" and "it works" has
repeatedly turned out to be where the bugs live.

## [Unreleased]

### Added

- **M5Stack Tab5 support** (ESP32-P4, 1280×720). One source tree now
  builds for two boards behind a compile-time board layer: a layout
  profile, a board implementation (Wi-Fi bring-up, display orientation,
  navigation input) and — only where a screen's *structure* differs — a
  renderer. About 310 lines per board against 3,377 shared.
  `firmware/core2/` became `firmware/display/`.
  **Not verified on hardware:** see Unverified below.
- **Nearby-traffic board** on the Tab5's FLIGHT page, listing the other
  aircraft in range. Costs nothing to produce — the provider already
  fetches and ranks them, and the Core2 simply had nowhere to put them.
- **Flight tracking.** Enter a flight number and arrival airport and the
  display follows that flight to touchdown: ETA, distance to go, and an
  explicit phase (waiting / enroute / descending / approaching / landed /
  no contact). Uses a direct `/v2/callsign` lookup rather than filtering
  a geographic sweep, on a poll interval that tightens from 5 minutes to
  10 seconds as arrival approaches.
- **"Leave now" departure prompt.** Given a travel time, the primary
  grid cell switches from when the aircraft lands to when *you* should
  set off. It accounts for the gap between touchdown and the person
  actually walking out — taxi, deplaning, immigration, bags — because an
  alert keyed to landing alone sends people to the airport 20–45 minutes
  early, which is the failure the feature exists to prevent.
- `CHANGELOG.md` (this file).

### Changed

- **Adaptive polling and response filtering.** Responses are parsed
  straight from the socket with an ArduinoJson field filter (13 of
  adsb.lol's ~51 fields per aircraft), measured at a 69.5% reduction on a
  real 114-aircraft response.
- **Redraw suppression.** Rendering ran unconditionally every 5 seconds
  even on screens static for long stretches; a "waiting for UA1234"
  display repainted ~480 identical frames an hour, each pushing ~1.84 MB
  over MIPI-DSI on the Tab5. A state signature now skips those, with a
  60-second forced repaint bounding the cost of anything the signature
  misses.
- **PWA initial load cut 57%** (535,943 → 228,793 bytes). Leaflet is only
  used by the display page and jsQR only by a scan path that cannot work
  over plain HTTP, yet every first-time user downloaded both before
  reaching the pairing form. Both are now lazy chunks.
- GitHub Actions updated to current majors (`checkout` v7, `setup-node`
  v7, `setup-python` v7), clearing the Node 20 deprecation warning.
- The protocol's `hello` role is now supplied by the caller rather than
  hardcoded to `"core2"`, so a board identifies itself as itself.
- `[env:core2]` is pinned to `espressif32@7.0.1`. See Fixed.

### Fixed

- **A configurable radius silently and permanently blanked the display.**
  The parse buffer is 16,384 bytes; a 270 NM radius — 500 km, which
  config validation explicitly accepted — returns 77,286 bytes and 133
  aircraft. Past roughly 60 NM the parse failed, provider health went
  degraded, and no aircraft was ever shown again with nothing pointing at
  the cause. Fixed by filtering, by clamping the *query* radius to 80 NM
  (rather than tightening validation, which would have failed
  already-saved configs on load), and by reporting parse failures on
  serial instead of swallowing them.
- **Building the Tab5 silently moved the Core2 onto a different
  toolchain.** pioarduino's manifest is also named `espressif32`, so a
  bare `platform = espressif32` resolved to whichever was installed last
  — taking the hardware-validated Core2 build onto Arduino core 3.3.5
  (flash 1,339,069 → 1,545,247 bytes) with no error.
- **`Core2StatusResponseSchema` required `gatewayConnectionState`**,
  which the firmware stopped sending when it became a standalone poller.
  `getCore2Status()` threw a `ZodError` against any real device.
- **A partial config `PUT` silently replaced the whole config.** The
  handler seeded only `deviceId` despite its own comment claiming partial
  updates were supported, so a brightness-only write would have cancelled
  a tracked flight.
- ADS-B callsigns are space-padded (`"BAW249  "`) — invisible in a
  left-aligned label, fatal to the comparison flight tracking makes on
  every poll. Now trimmed.

### Removed

- **`domain/protocol.cpp`** — a parser for the gateway's WebSocket frames,
  unreachable since the firmware became a standalone poller. Its
  4,136-byte `StaticJsonDocument` was the second-largest static
  allocation in the binary, carried on every boot for code nothing
  called. Removed with its 8 tests: a test suite for dead code is a
  liability, not coverage. Static RAM fell 3,736 bytes.
- **`DeviceConfiguration.gatewayUrl`** is deprecated and no longer sent.
  The firmware has never had a matching field and the gateway never read
  it; the setup wizard computed and transmitted it on every pairing for
  no effect. Kept optional in the schema so configs persisted by older
  builds still parse.

### Documentation

- `docs/ARCHITECTURE.md` claimed provider polling "all live[s] in the
  gateway" and that the device "only ever speaks plain WebSocket/HTTP,
  and only on the LAN". The firmware makes three direct HTTPS calls to
  adsb.lol. Both premises had expired — hardware was acquired, the design
  changed — but the document was never updated, so it spent several
  commits describing an architecture the code had already left. Rewritten
  to record what it used to say and why it changed.
- Test counts had gone stale (a claimed 107 total / 34 firmware against
  an actual 143 / 70). Every count is now obtained by running the suites.
- New: `docs/TAB5_HARDWARE.md`. `docs/CORE2_DISPLAY.md` became
  `docs/DISPLAY_UI.md` and covers both boards.

### Unverified

Listed explicitly because "it builds" has proven a weak signal here — the
Core2's own hardware testing turned up a stack overflow, missing CORS on
two APIs, and a config file that was silently never loaded, none of which
any amount of green CI had caught.

- **The M5Stack Tab5 has never been powered on.** Not boot, not panel
  init, not Wi-Fi via its ESP32-C6 co-processor, not layout, not touch
  navigation, not OTA. Every layout number was derived from panel
  geometry and font metrics rather than observed.
  `docs/TAB5_HARDWARE.md` lists what to check first, in likelihood order.
- **Flight tracking has never been watched through a real arrival.** The
  logic is unit-tested and the endpoints verified against the live API,
  but the phase transitions that matter most — landed versus lost
  contact — have not been seen against real data.
- No multi-day soak test, no Wi-Fi-outage test, no browser end-to-end
  tests.

## [0.1.0] — Phase 1 vertical slice

The starting point for this changelog: Wi-Fi provisioning, pairing, one
live data provider, and a single-aircraft airport-FIDS display on the
M5Stack Core2, plus a tablet PWA and a local gateway. Validated
end-to-end on physical Core2 hardware.

See `docs/FEATURE_PARITY_MATRIX.md` for what that did and didn't include,
and the git history before this file existed for the detail.
