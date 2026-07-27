# Core2 Display: Airport-FIDS Design

The Core2's screen is styled after a commercial airport Flight Information
Display System (FIDS) -- a departure-board look: strong hierarchy, tabular
alignment, restrained color, uppercase labels, minimal decoration. It is an
**original layout**, not a copy of any specific airport's board, airline
branding, or proprietary font -- see `docs/ATTRIBUTION.md`.

**Origin, destination, gate, and route are not available from ADS-B alone
and are intentionally not fabricated by this interface.** Every field shown
comes from what adsb.lol actually reports (or is computed from it, like
distance/bearing from the configured observer location).

## Resolution and orientation

320x240, landscape (`M5.Display.setRotation(1)`). Every layout constant in
`include/app/ui_layout.h` is a pixel value chosen for this exact resolution
-- nothing is scaled from a larger design.

## Screen states

Each is a distinct `Display::render*()` method (`include/app/display.h`) --
there is no generic "loading" spinner; every failure mode has an explicit,
readable screen. Selected by `renderCurrentState()` in `src/main.cpp`:

| State | Method | Shown when |
|---|---|---|
| Boot | `renderBoot` | Immediately at power-on, before Wi-Fi/config load |
| Wi-Fi setup | `renderProvisioning` | No stored Wi-Fi credentials (AP mode) |
| Setup required | `renderLocationRequired` | Wi-Fi connected, but not yet paired (no monitoring area configured) |
| Wi-Fi offline | `renderWifiOffline` | Was connected, `WiFi.status()` now reports link-down |
| ADS-B unavailable | `renderApiError` | Provider health is `Unavailable`, or an aircraft has been stale for 5x the stale threshold with no recovery |
| Searching | `renderSearching` | Configured and connected, but no aircraft update has ever arrived |
| No traffic | `renderNoTraffic` | Provider healthy, zero aircraft currently within the configured radius |
| Nearest aircraft | `renderAircraft` | The main screen -- an aircraft is known, fresh or briefly stale |
| Firmware update | `renderOtaProgress` | OTA in progress |

### Staleness: preserve, don't hide

When the nearest aircraft's position ages past `kStalePositionThresholdMs`
(60s, `domain/staleness.h`), the display does **not** blank the screen or
switch to a text-only status page. It keeps showing the same aircraft --
callsign, airline, last known altitude/speed/track -- and flips only the
STATUS grid cell to an amber **STALE** badge, plus the small age caption
next to the ICAO line (`12s`, `47s`, ...). Only if an aircraft stays stale
for `kSuperStaleMs` (5x the threshold, i.e. 5 minutes, `src/main.cpp`) with
no new data does the screen fall back to `renderSearching`. This mirrors
how a real departure board doesn't go blank the instant one update is
late -- it keeps the last known information visible and flags it.

An active emergency squawk always overrides the STATUS cell regardless of
staleness (`DisplayStatus::Emergency` beats `Stale` beats the aircraft's
ordinary phase-of-flight in `domain/display_format.cpp`'s status
precedence) -- see "Status precedence" below.

## Layout

```
┌──────────────────────────────────────────────────────────────┐
│ NEAREST AIRCRAFT                              📶  🔋 100%    │ y: 0-30  (header)
├──────────────────────────────────────────────────────────────┤ y: 30    (1px accent rule)
│ UAL1234                                                       │ y: 32-78 (callsign, largest text)
│ United Airlines                                    [ B738 ]   │ y: 78-100 (airline / type badge)
│ ICAO A1B2C3                                          LIVE     │ y: 100-114 (ICAO / freshness)
├──────────────────────┬──────────────────────┬─────────────────┤ y: 114 (divider)
│ DIST                 │ ALT                  │ SPEED           │
│ 6.8 NM               │ 12,450 FT            │ 286 KT          │ y: 118-179 (grid row 1)
├──────────────────────┼──────────────────────┼─────────────────┤ y: 179 (row divider)
│ TRACK                │ V/S                  │ STATUS          │
│ 247°                 │ +1,250               │ ┃ AIRBORNE      │ y: 179-240 (grid row 2)
│ WSW                  │ FT/MIN               │                 │
└──────────────────────┴──────────────────────┴─────────────────┘
```

Every region above is a named constant in `include/app/ui_layout.h`
(`kHeaderH`, `kCallsignY`, `kGridColBoundaries`, ...) -- `display.cpp`
never contains a bare layout number.

### Status precedence (STATUS grid cell)

1. **EMERGENCY** (red) -- `AircraftState.emergencyState != None`
2. **STALE** (amber) -- position older than the stale threshold
3. Phase of flight (green/neutral) -- `GROUND` / `CLIMB` / `DESCENT` /
   `LEVEL` / `AIRBORNE`, derived from `onGround` + vertical rate
   (`domain/display_format.cpp::classifyMotionStatus`, ±300 ft/min
   deadband around level flight)

## Typography

Three font **roles**, five concrete sizes, all from the GFXFF `FreeSans` /
`FreeSansBold` family already linked in by M5GFX (GNU FreeFont project,
licensed GNU GPL v3 with the font-embedding exception -- embedding the
glyphs in compiled firmware and distributing the binary is explicitly
permitted by that exception; no new font files were added to this repo).
The 4th visual style is the panel's own built-in bitmap font (zero extra
flash, already linked regardless):

| Role | Font(s) | Used for |
|---|---|---|
| A -- Identifier | `FreeSansBold24pt7b`, fallback `FreeSansBold18pt7b` | Callsign only |
| B -- Value | `FreeSansBold12pt7b` / `FreeSansBold9pt7b`, `FreeSans9pt7b` | Grid values, STATUS word, type badge, airline name |
| C -- Micro label | Built-in bitmap font (`fonts::Font0`), size 1 or 2 | All uppercase field labels, header, units, freshness caption |

Glyph coverage needed: `A-Z 0-9 space - / : . , + % `. The GFXFF fonts
cover ASCII 0x20-0x7E, which is everything above **except the degree
sign** (0xB0 is outside that range). Rather than pull in an extended
character set for one glyph, the degree mark is drawn as a small hollow
circle primitive (`drawDegreeMark` in `display.cpp`) positioned after the
track's digits -- consistent with the brief's preference for primitives
over adding font/asset weight.

### Fitting text, not guessing it

Every dynamic string is measured with the real font's `textWidth()` at
draw time (`drawFitText` in `display.cpp`) -- never estimated from
character count. The fit strategy, in order:

1. Draw at the primary font if it fits the reserved width.
2. Fall back to a smaller predefined font (callsign, STATUS word) if one
   is defined for that field.
3. Ellipsize (`…`) at whichever font is currently active, shrinking one
   character at a time until it fits.

This is what handles the validation matrix's edge cases: an unusually
long callsign drops from 24pt to 18pt before ever truncating; a long
airline name (`All Nippon Airways`) fits at 9pt in the ~218px identity
column; `EMERGENCY`/`SEARCHING` in the STATUS cell fall back to the
smaller value font before ellipsizing.

## Color tokens

All named in `include/app/ui_theme.h` -- `display.cpp` never contains a
raw RGB565 literal.

| Token | Meaning |
|---|---|
| `COLOR_BACKGROUND` | Main background, very dark navy |
| `COLOR_HEADER_BG` | Header band, slightly lighter navy |
| `COLOR_TEXT_PRIMARY` | Callsign, primary values |
| `COLOR_TEXT_SECONDARY` | Labels, airline name, unit captions |
| `COLOR_TEXT_DIM` | ICAO line, footnotes |
| `COLOR_GRID` | Separator lines, badge borders |
| `COLOR_ACCENT` | Header accent rule, type badge text, OTA progress fill |
| `COLOR_GOOD` | Live/connected/normal-flight-phase |
| `COLOR_CAUTION` | Stale data, low battery (10-19%) |
| `COLOR_CRITICAL` | Emergency, critical battery (<10%), Wi-Fi-offline slash |

Red is reserved for `COLOR_CRITICAL` states only -- normal aircraft data
is never shown in red, per the design brief.

## Battery indicator

Header, top-right, fixed-width reserved zone so `100%` never shifts the
Wi-Fi icon or title (`kHeaderBatteryPercentZoneW` in `ui_layout.h`).
Outline + proportional fill (green ≥20%, amber 10-19%, red <10%), a small
drawn bolt overlay when charging, and a `?` glyph when the PMIC read is
invalid. Sourced from the existing cached `BatteryState` in `AppContext`
(`src/app/battery_monitor.cpp` still polls the AXP192 on its own ~10s
timer, unchanged) -- the display never talks to the power IC directly and
never polls it per-frame.

## Wi-Fi status

Three ascending signal bars in the header, green when connected. This UI
adds one new piece of real behavior: `src/main.cpp`'s `loop()` now
actually checks `WiFi.status()` every 2s while connected (previously,
`AppContext.wifiState` was set once at boot and never re-evaluated, so a
runtime Wi-Fi drop was silently invisible to the whole app -- the
`WifiDisconnected` status existed in the old code but was unreachable
after initial connect). On a detected drop, the bars gray out with a
diagonal slash, the screen switches to `renderWifiOffline`, and
`WiFi.reconnect()` is called (ESP32 Arduino's WiFi does not auto-reconnect
on its own).

## Data freshness

Shown as a small caption next to the ICAO line: `LIVE` within one poll
interval, the exact age in seconds while aging (`domain/staleness.h`'s
threshold divided by 4, i.e. ≤15s), then `STALE` once the aircraft is
flagged stale. This is a passthrough of the same staleness check used for
the STATUS cell, formatted by `domain/display_format.cpp::formatDataAge`.

## Units

The airport display defaults to aviation units regardless of what other
surfaces (web portal) use: **nautical miles** for distance (converted
from the domain layer's internal km), **feet** for altitude (thousands
separator, `GROUND` when on-ground), **knots** for speed, **feet/minute**
signed for vertical rate.

## Rendering architecture

```
AircraftState (domain)         BatteryState (domain)
        │                              │
        ▼                              ▼
buildAircraftViewModel()      buildBatteryViewModel()      <- domain/display_format.{h,cpp}
        │                              │                       pure, hardware-independent,
        ▼                              ▼                       unit tested (test/native/test_display_format)
AircraftViewModel              BatteryViewModel
  (pre-formatted strings,
   fixed-size buffers,
   never "nan"/empty/raw)
        │
        ▼
Display::renderAircraft()     <- src/app/display.cpp
  (theme + layout + M5GFX
   drawing calls only --
   no parsing, no printf
   of raw domain fields)
```

`domain/display_format.h` defines the view models
(`AircraftViewModel`, `BatteryViewModel`) and normalization functions
(`classifyMotionStatus`, `formatDataAge`, ...) with zero Arduino/M5GFX
dependency, so formatting bugs (a wrong sign, a stray fallback that
doesn't fit its field, a mis-ordered status precedence) are caught by
`pio test -e native` rather than only visible on real hardware. See
`test/native/test_display_format/test_display_format.cpp` for the
covered cases (callsign fallback chain, altitude/speed/track/vertical-rate
formatting, battery tiers, status precedence, freshness thresholds).

`ui_theme.h` and `ui_layout.h` hold every color and pixel constant.
`display.cpp` itself is drawing-only: it builds a view model, then calls
theme/layout constants and small primitive helpers (`drawFitText`,
`drawWifiIcon`, `drawBatteryIcon`, `drawDegreeMark`, `drawGridFrame`,
`drawCellLabel`, `drawCellValueWithUnit`, `drawTrackCell`,
`drawStatusCell`).

## Sprite and buffering strategy

**Chosen: a small persistent header sprite (M5Canvas, 320x31, ~19.8KB as
RGB565) plus direct-to-panel region-clear-and-redraw for the body.**

This was a deliberate choice among the options considered:

- **Full 320x240 sprite** (~150KB): rejected. This specific Core2 unit
  has *confirmed* no PSRAM (`docs/CORE2_HARDWARE.md`), and the firmware
  already carries meaningful transient heap pressure elsewhere (mbedTLS
  HTTPS handshake to adsb.lol, ArduinoJson parsing of the response). A
  150KB *permanent* reservation was judged too large a bet to make
  without a measured `ESP.getFreeHeap()` baseline from real hardware,
  which wasn't available in this session (see "Remaining hardware
  checks" below).
- **Header + full body sprite** (~150KB total): same objection --
  the body sprite alone is ~130KB.
- **Dirty-region redraws** (chosen for the body): the aircraft screen
  redraws at most every `kRenderIntervalMs` (5s) in `main.cpp` -- far
  below any rate where SPI redraw time is perceptible as flicker. The
  body is cleared with one `fillRect` covering *only* the content area
  below the header (never `fillScreen()` across the whole panel, which
  is what caused the previous implementation's flicker), then redrawn.
  The header sprite exists mainly so the masthead (title, Wi-Fi, battery)
  stays visually stable and can be pushed independently of body redraws.

If flicker or tearing *is* visible on real hardware, the documented next
step is upgrading the body to its own persistent sprite -- every body
drawing call already goes through `M5.Display`, so swapping that
reference for a `M5Canvas&` and adding one `pushSprite()` call is a
small, contained change.

## Memory impact

Measured via `pio run -e core2` (same toolchain versions as
`docs/CORE2_HARDWARE.md`: espressif32 7.0.1, M5Unified 0.1.17):

| Metric | Before this redesign | After | Delta |
|---|---|---|---|
| Flash | 1,296,957 bytes (19.8%) | 1,331,105 bytes (20.3%) | +34,148 bytes (+0.5 pp) |
| RAM (static) | 82,812 bytes (1.8%) | 83,164 bytes (1.8%) | +352 bytes |

The flash delta is almost entirely the newly-referenced GFXFF font glyph
tables (5 fonts, ASCII-only subsets, ~8.8KB/5.2KB/2.9KB/1.9KB/1.8KB per
`docs/CORE2_HARDWARE.md`-style accounting). The RAM delta is the header
sprite's *object* overhead, not its pixel buffer -- `M5Canvas::createSprite()`
allocates its ~19.8KB pixel buffer from the heap at runtime (in
`Display::begin()`), not as static/global data, so it doesn't show up in
the linker's static RAM figure above. **Not yet measured on real
hardware:** actual free heap before/after that `createSprite()` call --
see "Remaining hardware checks."

`core2-ota`: 1,331,129 bytes (20.3% of the 6.25MB OTA slot) -- effectively
identical to `core2`, confirming ample headroom in either OTA slot.

## Unrelated fixes made along the way

Two pre-existing issues were found and fixed while restoring the ability
to run tests for this work (both predate this redesign):

1. **`[env:native]` had been deleted from `platformio.ini`** in the
   immediately prior commit (battery/OTA work), silently breaking
   `pio test -e native` for the whole project despite the README
   claiming all tests pass. Restored.
2. **`test/native/test_config/test_config.cpp`** referenced a
   `DeviceConfig.hasGatewayUrl` field and a `gatewayUrl` validation rule
   that no longer exist (removed when the Core2 became a standalone
   direct-adsb.lol-polling device, dropping the gateway-WebSocket
   config entirely). Removed the two obsolete assertions/tests so the
   suite compiles and reflects the current config schema.

**Found but intentionally not fixed** (out of scope for a display
redesign, and changing it without full context on the web portal's
partial-config-update flow risks a behavior change there):
`domain/config.cpp::parseAndValidateDeviceConfig` returns `true` even
when `deviceId` is entirely absent from the JSON payload (it's only
validated `if (doc.containsKey("deviceId"))`), which contradicts
`test/native/test_config/test_config.cpp`'s
`test_rejects_missing_device_id` (still failing). This looks like it may
be intentional support for partial config updates (e.g. a PUT that only
changes `brightness`), in which case the test is what's stale -- but
that's a judgment call for whoever owns the config/pairing-server
contract, not this task.

## How to add or adjust a field safely

1. Add the raw field to `domain/aircraft.h` (or wherever it's parsed)
   if it isn't already there.
2. Add formatting/fallback logic to `domain/display_format.h/.cpp`,
   producing a fixed-size, pre-formatted string on `AircraftViewModel`
   (never format inside `display.cpp`). Add a native test for at least
   the "missing" and one "present" case.
3. Add any new pixel region to `include/app/ui_layout.h` as a named
   constant -- never a bare number in `display.cpp`.
4. Add any new color to `include/app/ui_theme.h` as a named token.
5. Draw it in `display.cpp` using `textWidth()`-based measurement
   (`drawFitText` or the same pattern), not a hardcoded character-count
   estimate.
6. Run `pio test -e native`, then `pio run -e core2`, and check the
   flash/RAM delta before considering it done.

## Remaining hardware checks

Nothing in this session was verified on the physical Core2 -- no device
was connected. Before considering this redesign done in practice:

- Flash and visually inspect: legibility, brightness, alignment,
  clipping, at every screen state (boot, provisioning, setup-required,
  Wi-Fi-offline, no-traffic, searching, nearest-aircraft, OTA).
- Confirm the header sprite's `createSprite()` call doesn't push free
  heap uncomfortably low during an active HTTPS poll (log
  `ESP.getFreeHeap()` before/after, per the "Memory impact" section).
- Confirm no visible flicker/tearing on the body's region-clear redraw
  at the real 5s cadence, especially right as an HTTPS poll completes.
- Pull Wi-Fi (router power-off) to confirm the new `renderWifiOffline`
  screen and `WiFi.reconnect()` behavior actually recovers on a real
  network, not just compiles.
- Confirm OTA still completes end-to-end with the restyled progress
  screen.
