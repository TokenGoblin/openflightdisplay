# Device Display: Airport-FIDS Design

The firmware's screen is styled after a commercial airport Flight Information
Display System (FIDS) -- a departure-board look: strong hierarchy, tabular
alignment, restrained color, uppercase labels, minimal decoration. It is an
**original layout**, not a copy of any specific airport's board, airline
branding, or proprietary font -- see `docs/ATTRIBUTION.md`.

**Origin, destination, gate, and route are not available from ADS-B alone
and are intentionally not fabricated by this interface.** Every field shown
comes from what adsb.lol actually reports (or is computed from it, like
distance/bearing from the configured observer location).

## Supported panels

One source tree (`firmware/display/`) builds for two boards. What differs
is geometry and type size, not design language: the same palette, the same
screen states, the same information in the same reading order.

| | M5Stack Core2 | M5Stack Tab5 |
|---|---|---|
| Panel | 320x240 | 1280x720 |
| Layout profile | `include/app/ui_layout_core2.h` | `include/app/ui_layout_tab5.h` |
| Font scale | 1x throughout | 2-3x (same typefaces) |
| FLIGHT page | Nearest aircraft only | Nearest aircraft + nearby-traffic board |
| Page navigation | BtnA/BtnB/BtnC | Taps on the tab bar |
| Buffering | Header sprite + direct body draw | Full-screen PSRAM canvas |
| Hardware-validated | **Yes** (`docs/CORE2_HARDWARE.md`) | **No** (`docs/TAB5_HARDWARE.md`) |

Both profiles define the same constant names, so the shared renderer in
`src/app/display.cpp` compiles against either. A name that exists in one
profile and not the other is a build error the first time shared code
touches it. `include/app/ui_layout.h` picks the profile from the
`OFD_BOARD_*` flag the environment sets and `static_assert`s that it
agrees with the board traits in `include/board/board.h`.

## Screen states

Each is a distinct `Display::render*()` method (`include/app/display.h`) --
there is no generic "loading" spinner; every failure mode has an explicit,
readable screen. Selected by `renderCurrentState()` in `src/main.cpp`.
Every state exists on both boards:

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
| Flight detail | `renderAircraftDetail` / `renderDetailPlaceholder` | DETAIL tab |
| System | `renderSystemInfo` | SYSTEM tab |
| Firmware update | `renderOtaProgress` | OTA in progress |

`renderAircraft` is the **only** one implemented per board
(`src/app/display_flight_core2.cpp`, `src/app/display_flight_tab5.cpp`) --
it is the one screen whose structure, not just its proportions, differs.
Everything else is shared code driven by the layout profile.

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

## Layout: Core2 (320x240)

```
┌──────────────────────────────────────────────────────────────┐
│ NEAREST AIRCRAFT                              📶  🔋 100%    │ y: 0-30  (header)
├──────────────────────────────────────────────────────────────┤ y: 30    (1px accent rule)
│ UAL1234                                                       │ y: 32-78 (callsign, largest text)
│ United Airlines                                    [ B738 ]   │ y: 78-100 (airline / type badge)
│ ICAO A1B2C3                                          LIVE     │ y: 100-114 (ICAO / freshness)
├──────────────────────┬──────────────────────┬─────────────────┤ y: 114 (divider)
│ DIST                 │ ALT                  │ SPEED           │
│ 6.8 NM               │ 12,450 FT            │ 286 KT          │ y: 118-170 (grid row 1)
├──────────────────────┼──────────────────────┼─────────────────┤ y: 170 (row divider)
│ TRACK                │ V/S                  │ STATUS          │
│ 247°                 │ +1,250               │ ┃ AIRBORNE      │ y: 170-222 (grid row 2)
│ WSW                  │ FT/MIN               │                 │
├──────────────┬───────┴───────┬──────────────┴─────────────────┤ y: 222 (tab bar)
│    FLIGHT    │    DETAIL     │    SYSTEM                      │
└──────────────┴───────────────┴────────────────────────────────┘ y: 240
```

## Layout: Tab5 (1280x720)

The hero column is the Core2's screen re-proportioned -- same identity
block, same six-cell grid, same reading order. The right-hand column is
what the extra area buys.

```
┌───────────────────────────────────────────────┬──────────────────────────┐
│ NEAREST AIRCRAFT                       📶 🔋 100%                        │ y: 0-64 (header)
├───────────────────────────────────────────────┼──────────────────────────┤ y: 64 (accent rule)
│ UAL1234                                       │ NEARBY TRAFFIC           │ y: 80-248 (callsign)
│                                               │ ──────────────────────── │ y: 124 (divider)
│ United Airlines                     [ B738 ]  │ FLIGHT        NM     FT  │ y: 132 (captions)
│ ICAO A1B2C3                            LIVE   │ DAL22        4.1  12,000 │
├──────────────────┬──────────────────┬─────────┤ │ SWA891     6.8  24,000 │ y: 340 (divider)
│ DIST             │ ALT              │ SPEED   │ │ ASA455     9.2  31,000 │
│ 6.8 NM           │ 12,450 FT        │ 286 KT  │ │ FDX18     11.0  35,000 │ y: 344-504
├──────────────────┼──────────────────┼─────────┤ │ ...                    │
│ TRACK            │ V/S              │ STATUS  │ │                        │
│ 247° WSW         │ +1,250 FT/MIN    │ ┃ LEVEL │ │                        │ y: 504-664
├─────────────────────┬─────────────────────────┴─┬────────────────────────┤ y: 664 (tab bar)
│       FLIGHT        │        DETAIL             │       SYSTEM           │
└─────────────────────┴───────────────────────────┴────────────────────────┘ y: 720
```

The traffic board costs nothing to produce: `AdsbProvider` already fetches
every aircraft inside the configured radius and `rankNearest()` already
sorts them by distance, up to `kMaxAircraftPerUpdate` (10). The Core2
simply discards everything past `items[0]` because it has nowhere to put
it. `display_flight_tab5.cpp` reads the same `AircraftList` off the
ambient `AppContext` -- the pattern the header (battery/Wi-Fi) and the tab
bar (current page) already use -- which is why `Display::renderAircraft`
still takes only the nearest aircraft and needs no board-specific
signature. Rows carry the same emergency color coding the hero's STATUS
cell would give them; when there is only one aircraft in range the column
says `NO OTHER AIRCRAFT` rather than sitting empty.

Every region in both diagrams is a named constant in the board's layout
profile (`kHeaderH`, `kCallsignY`, `kGridColBoundaries`, ...) -- the
renderer never contains a bare layout number.

## Page navigation

Three pages -- FLIGHT, DETAIL, SYSTEM -- selected by a tab bar drawn at
the bottom of every operational-state screen, so navigation works the same
way regardless of what's currently shown. Each control jumps *directly* to
one page rather than cycling prev/next, so a given gesture always means
the same thing.

*How* a page is selected is board-specific and lives behind
`board::pollPageRequest()`:

- **Core2** maps BtnA/BtnB/BtnC to the three tab columns they sit under.
- **Tab5** has no buttons; it hit-tests taps against the tab bar, which is
  56px tall for a comfortable finger target. It uses `wasClicked()` (press
  and release inside the same area) rather than `wasPressed()`, so
  dragging a finger across the bar or resting one on it while picking the
  tablet up doesn't count as a tap.

`main.cpp`'s `loop()` only asks "did the user request a different page"
and repaints immediately on a change rather than waiting up to
`kRenderIntervalMs`.

### Status precedence (STATUS grid cell)

1. **EMERGENCY** (red) -- `AircraftState.emergencyState != None`
2. **STALE** (amber) -- position older than the stale threshold
3. Phase of flight (green/neutral) -- `GROUND` / `CLIMB` / `DESCENT` /
   `LEVEL` / `AIRBORNE`, derived from `onGround` + vertical rate
   (`domain/display_format.cpp::classifyMotionStatus`, ±300 ft/min
   deadband around level flight)

## Typography

Three font **roles**, all from the GFXFF `FreeSans` / `FreeSansBold` family
already linked in by M5GFX (GNU FreeFont project, licensed GNU GPL v3 with
the font-embedding exception -- embedding the glyphs in compiled firmware
and distributing the binary is explicitly permitted by that exception; no
new font files were added to this repo), plus the panel's own built-in
bitmap font (zero extra flash, already linked regardless).

A role is a **(typeface, integer scale) pair**, not a bare font pointer:
the two panels are 4x apart in linear resolution and M5GFX only bundles
FreeSans up to 24pt, so the Tab5 reaches its display sizes by scaling the
same typefaces. Both boards therefore render identical shapes at
proportional sizes, and the renderer has exactly one set of role names to
reason about. `theme::applyFont()` sets typeface and scale together --
calling `setFont()` alone leaves a stale `setTextSize()` behind, a mistake
that stays invisible on the board where every role is scale 1.

| Role | Typeface(s) | Core2 | Tab5 | Used for |
|---|---|---|---|---|
| A -- Identifier | `FreeSansBold24pt7b` (fallback `FreeSansBold18pt7b` on Core2, same face at 2x on Tab5) | 1x | 3x | Callsign only |
| B -- Value | `FreeSansBold12pt7b` / `FreeSansBold9pt7b`, `FreeSans9pt7b` | 1x | 2-3x | Grid values, STATUS word, type badge, airline name |
| C -- Micro label | Built-in bitmap font (`fonts::Font0`) | 1x / 2x | 3x / 4x | Uppercase field labels, header, units, freshness caption, traffic rows |

Glyph coverage needed: `A-Z 0-9 space - / : . , + % `. The GFXFF fonts
cover ASCII 0x20-0x7E, which is everything above **except the degree
sign** (0xB0 is outside that range). Rather than pull in an extended
character set for one glyph, the degree mark is drawn as a small hollow
circle primitive (`drawDegreeMark`) positioned after the track's digits --
consistent with the brief's preference for primitives over adding
font/asset weight. Its radius and offsets are layout-profile constants, so
it scales with the rest of the design.

### Fitting text, not guessing it

Every dynamic string is measured with the real font's `textWidth()` at
draw time (`draw::drawFitText`) -- never estimated from character count.
The fit strategy, in order:

1. Draw at the primary role if it fits the reserved width.
2. Fall back to a smaller predefined role (callsign, STATUS word) if one
   is defined for that field.
3. Ellipsize (`…`) at whichever role is currently active, shrinking one
   character at a time until it fits.

This is what handles the validation matrix's edge cases: an unusually
long callsign drops a size before ever truncating; a long airline name
(`All Nippon Airways`) fits in the identity column; `EMERGENCY` /
`SEARCHING` in the STATUS cell fall back to the smaller value role before
ellipsizing. Because the same code runs on both boards, a string that fits
one panel and not the other degrades rather than overflows.

## Color tokens

All named in `include/app/ui_theme.h` -- the renderer never contains a
raw RGB565 literal. The palette is deliberately **board-independent**: the
two boards show the same product and should look like it.

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
Wi-Fi icon or title (`kHeaderBatteryPercentZoneW`). Outline + proportional
fill (green ≥20%, amber 10-19%, red <10%), a small drawn bolt overlay when
charging, and a `?` glyph when the power-IC read is invalid. Sourced from
the cached `BatteryState` in `AppContext` (`src/app/battery_monitor.cpp`
polls on its own ~10s timer) -- the display never talks to the power IC
directly and never polls it per-frame.

The icon's outline, nub, fill inset and bolt thickness are all layout
profile constants, so the same drawing code produces a crisp 20x11 icon on
the Core2 and a 52x28 one on the Tab5 rather than a scaled-up blur.

## Wi-Fi status

Three ascending signal bars in the header, green when connected.
`src/main.cpp`'s `loop()` checks `WiFi.status()` every 2s while connected
(previously `AppContext.wifiState` was set once at boot and never
re-evaluated, so a runtime Wi-Fi drop was silently invisible to the whole
app). On a detected drop, the bars gray out with a diagonal slash, the
screen switches to `renderWifiOffline`, and `WiFi.reconnect()` is called
(ESP32 Arduino's WiFi does not auto-reconnect on its own).

On the Tab5 this all works the same way, but the radio is a separate
ESP32-C6 co-processor reached over SDIO -- see `docs/TAB5_HARDWARE.md`.
The reconnect path in particular has not been exercised on that board.

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
Display::render*()            <- src/app/display.cpp (shared states)
        │                        src/app/display_flight_{core2,tab5}.cpp (FLIGHT page)
        ▼
draw::* primitives            <- src/app/display.cpp, declared in app/display_draw.h
        │                        (theme + layout + M5GFX drawing calls only --
        ▼                         no parsing, no printf of raw domain fields)
draw::gfx()                   <- the panel, or a full-screen canvas
```

`domain/display_format.h` defines the view models
(`AircraftViewModel`, `BatteryViewModel`) and normalization functions
(`classifyMotionStatus`, `formatDataAge`, ...) with zero Arduino/M5GFX
dependency, so formatting bugs (a wrong sign, a stray fallback that
doesn't fit its field, a mis-ordered status precedence) are caught by
`pio test -e native` rather than only visible on real hardware. See
`test/native/test_display_format/test_display_format.cpp` for the
covered cases.

`app/display_draw.h` is the internal contract between the shared screen
states and the per-board FLIGHT renderers: `drawHeader`, `drawTabBar`,
`drawIdentityBlock`, `drawMetricGrid`, `drawStatusBody`, `drawDetailRow`,
`drawFitText`, and the frame lifecycle (`gfx()`, `endFrame()`). Nothing
outside `src/app/` includes it; `app/display.h` is the public interface.

## Sprite and buffering strategy

Two strategies, selected by `board::kUseFullScreenCanvas`. Both are hidden
behind `draw::gfx()` / `draw::endFrame()`, so no drawing code branches on
which one is active.

**Core2 -- small persistent header sprite (M5Canvas, 320x31, ~19.8KB as
RGB565) plus direct-to-panel region-clear-and-redraw for the body.**
Considered and rejected: a full 320x240 sprite (~150KB). That unit has
*confirmed* no PSRAM (`docs/CORE2_HARDWARE.md`) and the firmware already
carries meaningful transient heap pressure elsewhere (mbedTLS HTTPS
handshake to adsb.lol, ArduinoJson parsing of the response); a 150KB
*permanent* reservation was judged too large a bet without a measured
`ESP.getFreeHeap()` baseline. The body redraws at most every
`kRenderIntervalMs` (5s), far below any rate where SPI redraw time is
perceptible as flicker, and is cleared with one `fillRect` covering *only*
the content area below the header -- never `fillScreen()` across the whole
panel, which is what caused an earlier implementation's flicker.

**Tab5 -- full-screen 16-bit canvas in PSRAM (~1.84MB), composed
off-screen and pushed once per frame.** At 1280x720 a region-clear-and-
redraw would be a very visible wipe, and this board has 32MB of PSRAM. If
`createSprite()` fails, the renderer logs the failure on serial and falls
back to drawing directly to the panel: a device that renders with a
visible wipe is still a usable device, and a blank panel with no
explanation is the worst possible outcome of a PSRAM allocation that
didn't go as planned.

## Adding a board

The board layer (`include/board/board.h`) is deliberately the lowest-level
module in the firmware -- it depends on nothing above it, which is why
`app/page.h` exists to carry `DetailPage` without dragging in all of
`AppContext`. Adding a third board should mean:

1. `src/board/<name>.cpp` implementing `begin()` (hardware bring-up before
   any Wi-Fi call), `beginDisplay()` (rotation/brightness) and
   `pollPageRequest()` (navigation input).
2. `include/app/ui_layout_<name>.h` defining the same constant names as
   the existing profiles.
3. An `OFD_BOARD_<NAME>` block in `board.h` (traits) and a branch in
   `ui_layout.h` and `ui_theme.h`.
4. An `[env:<name>]` in `platformio.ini` with the right `build_src_filter`.
5. A `display_flight_<name>.cpp` **only if** the FLIGHT page's structure
   genuinely differs from an existing board's.

...and nothing else. `Display::begin()` checks the panel's real reported
size against the layout profile at boot and logs loudly on a mismatch,
which is the failure you'll hit first if a rotation is wrong.

## How to add or adjust a field safely

1. Add the raw field to `domain/aircraft.h` (or wherever it's parsed)
   if it isn't already there.
2. Add formatting/fallback logic to `domain/display_format.h/.cpp`,
   producing a fixed-size, pre-formatted string on `AircraftViewModel`
   (never format inside the renderer). Add a native test for at least
   the "missing" and one "present" case.
3. Add any new pixel region to **both** layout profiles as a named
   constant -- never a bare number in the renderer. Omitting one is a
   compile error, which is the point.
4. Add any new color to `include/app/ui_theme.h` as a named token.
5. Draw it using `textWidth()`-based measurement (`draw::drawFitText` or
   the same pattern), not a hardcoded character-count estimate.
6. Run `pio test -e native`, then `pio run -e core2` **and**
   `pio run -e tab5`, and check the flash/RAM delta before considering it
   done.

## Remaining hardware checks

The Core2 has been through the checks below on a physical unit; see
`docs/CORE2_HARDWARE.md` and `docs/TEST_PLAN.md` for what that covered and
what it didn't. **The Tab5 has been through none of them** -- it compiles,
and that is the entire claim. Before considering Tab5 support done:

- Flash and visually inspect every screen state (boot, provisioning,
  setup-required, Wi-Fi-offline, no-traffic, searching, nearest-aircraft,
  detail, system, OTA) for legibility, alignment and clipping. The layout
  numbers were derived from panel geometry and font metrics, not from
  looking at a panel.
- Confirm the panel actually reports 1280x720 at rotation 1 -- watch
  serial for the `PANEL SIZE MISMATCH` line `Display::begin()` emits.
- Confirm the full-screen canvas allocates (watch for the fallback
  warning) and that pushing ~1.84MB per frame at the 5s cadence doesn't
  visibly tear.
- Tap each of the three tabs, including near the column boundaries.
- Everything in `docs/TAB5_HARDWARE.md`'s "What to check first" list.
