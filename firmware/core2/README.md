# OpenFlightDisplay Core2 Firmware

PlatformIO project for the M5Stack Core2. See `docs/CORE2_HARDWARE.md`, `docs/PROTOCOL.md`, and `docs/PROVISIONING.md`.

## Layout

```
include/domain/   Hardware-independent headers (geo, aircraft, ranking, staleness, config, protocol, time_util)
src/domain/       Their implementations -- no Arduino/ESP32 dependency, compiles under the `native` env
src/app/          Wi-Fi provisioning, pairing server, display rendering, gateway WS client (ESP32-only)
src/main.cpp      Thin entry point (setup()/loop()) wiring the app layer together
test/native/      One PlatformIO test suite per domain area, run with `pio test -e native`
```

## Building

```
pio run -e core2          # build firmware for the real board
pio run -e core2 -t upload -t monitor   # flash + serial monitor (needs a connected Core2)
pio test -e native         # run domain-logic unit tests on your host machine -- no hardware needed
```

## Known limitation for this session

This firmware was written and reviewed but **not compiled or flashed** — the sandbox that produced it had no PlatformIO installation. Library names/versions in `platformio.ini` are believed current but unverified against the registry. Run `pio test -e native` yourself first (fastest feedback, no hardware) and see `docs/TEST_PLAN.md` for the full manual hardware-validation checklist before flashing a real device.

## Why domain/ and app/ are split

`include/domain` and `src/domain` contain zero Arduino/ESP32 headers on purpose, so they compile with a plain host compiler (`pio test -e native`) and can be unit tested without a board. `src/app` is where all the hardware/network-specific code (M5Unified display calls, WiFi/DNSServer/ESPAsyncWebServer, LittleFS, the WebSocket client) lives, and it's excluded from the native test build (see `platformio.ini`'s `build_src_filter`).
