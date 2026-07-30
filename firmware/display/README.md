# OpenFlightDisplay Device Firmware

One PlatformIO project, two boards. See `docs/DISPLAY_UI.md` for the
screen design, `docs/CORE2_HARDWARE.md` / `docs/TAB5_HARDWARE.md` for
per-board notes, and `docs/PROTOCOL.md` / `docs/PROVISIONING.md` for the
wire contract and setup flow.

| Board | Env | Panel | Status |
|---|---|---|---|
| M5Stack Core2 (ESP32) | `core2`, `core2-ota` | 320×240 | Hardware-validated end to end |
| M5Stack Tab5 (ESP32-P4) | `tab5`, `tab5-ota` | 1280×720 | **Compiles only — never run on a physical unit** |

## Layout

```
include/domain/   Hardware-independent headers (geo, aircraft, ranking, staleness, config, protocol, time_util)
src/domain/       Their implementations -- no Arduino/ESP32 dependency, compiles under the `native` env
include/board/    board.h: per-board traits + the three hooks that differ between boards
src/board/        One .cpp per board; exactly one is compiled into any given build
include/app/      ui_layout.h (dispatcher) + ui_layout_<board>.h profiles, ui_theme.h, display_draw.h
src/app/          Wi-Fi provisioning, pairing server, adsb.lol poller, battery, display rendering (ESP32-only)
src/main.cpp      Thin entry point (setup()/loop()) wiring the app layer together
test/native/      One PlatformIO test suite per domain area, run with `pio test -e native`
```

## Building

```
pio test -e native                      # domain-logic unit tests on your host -- no hardware needed
pio run -e core2                        # build for the Core2
pio run -e tab5                         # build for the Tab5
pio run -e core2 -t upload -t monitor   # flash + serial monitor (needs a connected board)
```

**Windows:** build the `tab5` env from PowerShell or cmd, not Git Bash.
pioarduino fetches the RISC-V toolchain with `idf_tools.py`, which refuses
to run under MSys/MinGW. The symptom is
`'riscv32-esp-elf-g++' is not recognized`, which does not look like a
shell problem.

**Switching between boards:** the two Espressif platforms share one
`framework-arduinoespressif32` package directory and evict each other, so
the first `tab5` build after a `core2` build fails in ~10s with a
`TypeError: ... not 'NoneType'` from PlatformIO's `arduino.py`. **Run it
again and it succeeds** — reproducible, not flaky, and not your code.
`docs/TAB5_HARDWARE.md` § "Two platforms in one PlatformIO installation"
has the measurements and the other two variants of this problem, including
why `[env:core2]` pins its platform to an exact version and why un-pinning
it silently changes what the Core2 is built from.

OTA uploads (`core2-ota` / `tab5-ota`) need a per-device `--upload-port` —
the device registers its own device id as its mDNS hostname so several
units can coexist on one network. Read it from the SYSTEM page.

## How the two boards share code

Everything above `src/board/` is board-agnostic. Each environment defines
exactly one `OFD_BOARD_*` flag, which selects:

- a **board implementation** (`src/board/<name>.cpp`) — hardware bring-up
  before any Wi-Fi call, display orientation, and page-navigation input;
- a **layout profile** (`include/app/ui_layout_<name>.h`) — every pixel
  region, by name. Both profiles define the same names, so the shared
  renderer compiles against either and a missing constant is a build
  error rather than a wrong-looking screen;
- a **set of font scales** (`include/app/ui_theme.h`) — same typefaces and
  the same palette on both boards, scaled for the panel.

The only screen implemented per board is the FLIGHT page
(`src/app/display_flight_{core2,tab5}.cpp`), because it's the only one
whose structure rather than proportions differs: the Tab5 adds a
nearby-traffic board next to the nearest aircraft. Every other screen
state is shared code driven by the layout profile.

`docs/DISPLAY_UI.md`'s "Adding a board" section is the checklist for a
third one.

## Why domain/ and app/ are split

`include/domain` and `src/domain` contain zero Arduino/ESP32 headers on
purpose, so they compile with a plain host compiler (`pio test -e native`)
and can be unit tested without a board. `src/app` is where all the
hardware/network-specific code (M5Unified display calls,
WiFi/DNSServer/ESPAsyncWebServer, LittleFS, the HTTPS poller) lives, and
it — along with `src/board` — is excluded from the native test build (see
`platformio.ini`'s `build_src_filter`).

`include/app/page.h` exists for the same reason in miniature: the board
layer needs to name `DetailPage` without depending on `AppContext`.

## Status

- `pio test -e native` — **71/71 domain-logic tests pass.**
- `pio run -e core2` — builds and links: 1,339,069 bytes flash (20.4% of
  the 6.25MB OTA slot), 85,268 bytes static RAM. A physical Core2 has been
  flashed and run end to end (provisioning, pairing, live adsb.lol data);
  see `docs/CORE2_HARDWARE.md` and `docs/TEST_PLAN.md` for what that
  covered and what it didn't.
- `pio run -e tab5` — builds and links: 1,485,826 bytes flash (22.7%),
  72,372 bytes static internal SRAM. **Nothing beyond that has been
  verified** — no Tab5 has been connected. See `docs/TAB5_HARDWARE.md`.
