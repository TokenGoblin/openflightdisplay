# Feature Parity Matrix

Columns: Feature | Commercial-category behavior | Core2 support | Tablet support | Gateway required | Data-source dependency | MVP phase | Status | Test coverage | Notes

Status legend: `done` (Phase 1 shipped), `planned` (designed, not built), `future` (acknowledged, not designed in detail yet).

**On the "Core2" column and the M5Stack Tab5.** The firmware is one source tree that builds for both boards, and every feature below is implemented in board-independent code — so read the Core2 column as "device firmware support" throughout. It is *not* relabelled here for one reason: every `done` in that column is backed by a Core2 that was flashed and run, and the Tab5 has never been connected to anything. Renaming the column would silently extend those claims to hardware nobody has tested. The Tab5 gets its own column when it has its own evidence; until then see `docs/TAB5_HARDWARE.md`.

The one feature where the two boards genuinely differ rather than merely sharing code is the FLIGHT screen: the Tab5's larger panel also shows a nearby-traffic board listing the other aircraft in range, which the provider already fetches and ranks (`docs/DISPLAY_UI.md`).

## Provisioning and device management

| Feature | Category behavior | Core2 | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| First-boot setup screen | Guided onboarding | Yes | Yes | No | None | 1 | done | Native (boot state machine) + PWA wizard tests | |
| Unique device ID | Stable identity | Yes | Yes (displays it) | No | None | 1 | done | Native config tests | Generated from chip ID |
| QR code pairing payload | Fast pairing | Yes (renders) | Yes (attempts scan) | No | None | 1 | done, but confirmed non-functional in practice | Manual hardware test (real phone) + native QR-payload format test | Renders correctly, but a phone's default camera app just opens the link, and the PWA's in-app scanner can't run at all without HTTPS (`navigator.mediaDevices`) — see ARCHITECTURE.md/PROVISIONING.md. Not the working pairing path. |
| SoftAP + captive portal | No-app Wi-Fi setup | Yes | N/A | No | None | 1 | done | Manual hardware test (real Core2, executed) | |
| Normal LAN mode | Steady-state operation | Yes | Yes | No | None | 1 | done | Manual hardware test (real Core2, executed) | |
| mDNS discovery (Core2-side) | Zero-config LAN discovery | Yes (advertises) | No (browsers can't browse mDNS) | No | None | 1 | done (Core2 only) | Manual | PWA relies on manual entry instead (QR doesn't work in practice) — documented in ARCHITECTURE.md |
| Manual IP entry | Primary pairing path | N/A | Yes | No | None | 1 | done | PWA unit test + real hardware test | Confirmed via hardware testing to be the pairing method that actually works; not a fallback |
| Pairing code | Short-lived auth for first config write | Yes | Yes | No | None | 1 | done | Native + PWA tests | |
| Multiple-device support | Fleet management | Yes | Planned | Yes | None | 5 | future | — | |
| Device naming | Friendly labels | Yes | Yes | No | None | 1 | done | Config tests | |
| Online/offline indicator | At-a-glance health | Yes | Yes | Yes (gateway tracks heartbeat) | None | 1 | done | WS heartbeat tests | |
| Firmware version display | Diagnostics | Yes | Yes | No | None | 1 | done | Native test | |
| OTA updates | Fleet maintainability | Planned | Planned | Yes | None | 5 | future | — | |
| Factory reset | Recovery | Yes | Yes (triggers) | No | None | 2 | planned | — | |
| Export/import configuration | Portability | No | Yes | Yes | None | 3 | planned | — | |
| Recovery from interrupted provisioning | Reliability | Yes | N/A | No | None | 1 | done | Native (config-write atomicity) | Atomic LittleFS write |
| Recovery from bad Wi-Fi credentials | Reliability | Yes | N/A | No | None | 1 | done | Manual | Falls back to SoftAP after N failed connect attempts |
| Open / WPA2 / WPA3-transition Wi-Fi | Hardware compatibility | Yes (WPA2 verified only; WPA3 support is an ESP32 SDK capability, unverified on this hardware) | N/A | No | None | 1 | done (WPA2), unverified (WPA3) | Manual | See CORE2_HARDWARE.md |

## Location and monitoring areas

| Feature | Category behavior | Core2 | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Manual lat/lon entry | Baseline location input | No | Yes | Yes (stores) | None | 1 | done | PWA + gateway tests | |
| Browser geolocation w/ permission | Convenience | No | Yes | Yes | None | 1 | done | PWA test (mocked geolocation) | Explicit permission prompt, never silent |
| Map pin placement | Visual location picking | No | Planned | Yes | None | 2 | planned | — | |
| Address/geocoding lookup | Convenience | No | Planned | Yes | Geocoding provider | 2 | planned | — | |
| Configurable radius (circular area) | Core monitoring config | Yes | Yes | Yes | None | 1 | done | Native + gateway + PWA tests | |
| Min/max altitude filter | Relevance tuning | Yes | Yes | Yes | None | 2 | planned | — | |
| Polygon area | Precise geofencing | No | Planned | Yes | None | 3 | planned | — | |
| Directional field-of-view cone | Precise geofencing | No | Planned | Yes | None | 3 | planned | — | |
| Multiple monitoring areas | Flexibility | Planned | Planned | Yes | None | 3 | planned | — | |
| Named/saved areas | Usability | No | Planned | Yes | None | 3 | planned | — | |
| Enable/disable areas | Flexibility | No | Planned | Yes | None | 3 | planned | — | |
| Local timezone | Correct clock/idle mode | Yes | Yes | No | None | 2 | planned | — | |
| Home/anchor airport | Airport mode | No | Planned | Yes | Enrichment provider | 3 | planned | — | |
| Units (metric/imperial/aviation) | Localization | Yes (metric only in Phase 1) | Yes (metric only) | No | None | 2 | planned (metric done in 1) | Native unit-conversion tests planned | |

## Aircraft filters

| Feature | Category behavior | Core2 | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Airborne vs. ground | Basic relevance | Yes (via `onGround` field) | Yes | Yes | Provider must report it | 2 | planned | Model test done (field exists), filter logic planned | `AircraftState.onGround` already modeled |
| Max distance | Basic relevance | Yes (= radius) | Yes | Yes | None | 1 | done | Ranking tests | Radius doubles as the Phase 1 distance filter |
| Min/max altitude | Relevance | No | No | No | None | 2 | planned | — | |
| Category (fixed-wing/rotor/glider/balloon/drone/ground vehicle) | Filtering | No | No | No | Provider category quality varies | 2 | planned | — | Depends on `aircraftCategory` field, provider-dependent completeness |
| Commercial / GA / private / military classification | Filtering | No | No | No | Provider-dependent, often unavailable/unreliable | 3 | planned | — | Never claim military classification without a provider that legitimately supports it |
| Emergency squawk | Safety-relevant filter/alert | No (alerting is Phase 3) | No | No | None | 3 | planned | — | Model field `emergencyState` exists now |
| Callsign / airline / origin / destination / type / registration / ICAO hex / squawk | Search & filter | No | Planned (search box) | Yes | Enrichment for airline/type names | 2/3 | planned | — | |
| Min/max speed, climb/descend/level, approaching/departing | Advanced filters | No | No | No | None | 3 | planned | — | |
| Favorites / excluded aircraft | Personalization | No | Planned | Yes | None | 3 | planned | — | |
| Aircraft with incomplete data | Data-quality handling | Yes (`dataQualityFlags`) | Yes | No | None | 1 | done (modeled), filter UI planned | Model test | Missing enrichment never suppresses a valid aircraft (per product brief) |

## Ranking

| Feature | Category behavior | Core2 | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Nearest horizontal distance | Default ranking | Yes | Yes | Yes | None | 1 | done | Native + gateway unit tests (haversine) | The only ranking mode implemented in Phase 1 |
| Nearest slant range | Altitude-aware ranking | No | No | No | None | 2 | planned | — | |
| Closest approach / highest / lowest / fastest / approaching-most-directly / newest / emergency-priority / favorites-first / weighted | Advanced ranking modes | No | No | No | None | 2/3 | planned | — | Weighted-relevance formula to be documented when implemented, per product brief |
| Stale-position handling | Trust/data-age | Yes | Yes | Yes | None | 1 | done | Native + gateway tests | Age indicator + explicit "Data is stale" state |

## Individual flight tracking / Airport mode

| Feature | Category behavior | Core2 | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Track by flight # / callsign | Core "follow a flight" use case | Yes | Yes (entry + live state) | No | None | 3 | done | Native (27 flight-tracking + 7 config) + PWA (12) | Direct `/v2/callsign` lookup on an adaptive cadence; ETA from position + groundspeed against a user-supplied destination. See `docs/DISPLAY_UI.md` |
| Track by registration / ICAO hex | Same, other identifiers | No | No | No | None | 3 | planned | — | Endpoints exist (`/v2/reg`, `/v2/hex`) and the poller is structured for them; only callsign matching is wired up |
| Arrival ETA | "When do I leave to collect someone" | Yes | Yes | No | None | 3 | done | Native ETA/phase tests | **Estimated from current groundspeed, not a published schedule** — ADS-B carries no timetable, and nothing here claims otherwise |
| "Leave now" alert | Departure prompt for a pickup | No | No | No | None | 3 | future | — | Needs the stubbed `AlertRule` model and a delivery path; ETA + countdown is shipped, the alert on top of it is not |
| Airport mode (departures/arrivals) | Category feature | No | No | No | Provider schedule data (rare/unreliable on ADS-B-only sources) | 3 | future | — | Must not claim schedule accuracy from position-only sources |

## Core2 display modes

| Feature | Category behavior | Core2 | Tablet (preview) | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|---|
| Single aircraft (glanceable) | Primary Core2 mode | Yes | No | Yes | None | 1 | done | Native render-state tests (mocked display) | |
| Compact list (3-5 aircraft) | Secondary mode | No | No | No | None | 2 | planned | — | |
| Flight board | Secondary mode | No | No | No | None | 2 | planned | — | |
| Minimal mode | Secondary mode | No | No | No | None | 2 | planned | — | |
| Tracked-flight mode | Secondary mode | Yes | Yes | No | None | 3 | done | Native + PWA tests | Takes over the primary page while a flight is being followed (tab relabels FLIGHT→TRACK), reverts on touchdown — so the board stays at three pages and the Core2's three-button mapping is unchanged |
| Clock/idle mode | No-data fallback | Yes | No | No | None | 1 | done | Native state-machine test | Shown when zero matching aircraft, never a blank/loading screen |
| Explicit status states (no-match / waiting / source-unavailable / wifi-down / config-required / stale) | Reliability requirement | Yes | Yes (mirrors) | Yes | None | 1 | done | Native + gateway + PWA tests | Core requirement, not deferred |

## Tablet display modes

| Feature | Category behavior | Tablet | Gateway req'd | Data-source dep | Phase | Status | Tests | Notes |
|---|---|---|---|---|---|---|---|---|
| Radar (map + aircraft symbols) | Primary tablet mode | Yes (basic: pin + card, Leaflet/OSM raster tiles) | Yes | None | 1 | done (basic) | PWA render tests w/ mock data | Range rings, trails, clustering, track-up are Phase 2/3 |
| Flight board | Secondary mode | No | No | None | 2 | planned | — | |
| Split view | Combined layout | No | No | None | 3 | planned | — | |
| Core2 preview (320×240 accurate render) | Configuration aid | No | No | None | 2 | planned | — | |
| Kiosk mode | Always-on display | Basic full-screen toggle only; burn-in mitigation/wake-lock/rotation interval are Phase 2 | Yes | None | 1 (basic) / 2 (full) | partial | Manual | |

## Display customization, enrichment, alerts, history, MQTT/HA

All tracked as `future`/`planned` per `docs/IMPLEMENTATION_PLAN.md` phases 2-5. Not designed in implementation detail yet; see the product brief sections these came from for the full checklist, which is preserved as the backlog rather than duplicated here. Re-visit this matrix at the start of each phase and expand the relevant section before implementation begins.

## Reliability (cross-cutting, Phase 1 baseline)

| Feature | Core2 | Gateway | Tablet | Status | Tests |
|---|---|---|---|---|---|
| Clean startup after power loss | Yes | Yes | N/A | done | Native config-load test |
| Automatic Wi-Fi reconnection | Yes | N/A | N/A | done | Manual |
| Automatic gateway/provider reconnection | Yes (WS client backoff) | Yes (provider backoff) | Yes (WS client backoff) | done | Native + gateway reconnect tests |
| No reboot loops from corrupt config | Yes | Yes | N/A | done | Native corrupt-config test |
| Atomic configuration writes | Yes | Yes | N/A | done | Native + gateway tests |
| Visible data-age indicator | Yes | — | Yes | done | Native + PWA tests |
| No permanent "preparing" state | Yes | — | Yes | done | Native + PWA state-machine tests |
