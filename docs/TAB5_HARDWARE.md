# Tab5 Hardware Notes

> **No physical M5Stack Tab5 was connected at any point.** Everything here
> was derived from M5Stack's published documentation, the pioarduino
> platform, and a build that compiles and links. Compare that with
> `docs/CORE2_HARDWARE.md`, where every claim is backed by a unit that was
> flashed and run end-to-end — and where the interesting content is the
> list of bugs that *only* showed up on real hardware. Assume this file
> has an equivalent list waiting to be written.
>
> Verified: `pio run -e tab5` succeeds. Not verified: literally everything
> else — that it boots, that the panel initialises, that Wi-Fi associates,
> that the layout looks right, that touch navigation works.

## Target hardware (from M5Stack's specifications, not measured)

- MCU: **ESP32-P4**, dual-core RISC-V, 360-400MHz. No Wi-Fi or Bluetooth
  on-die.
- Wireless: **ESP32-C6 co-processor** over SDIO, running esp-hosted slave
  firmware that M5Stack preloads at the factory. Wi-Fi 6.
- Display: **5", 1280x720 MIPI-DSI**. The display/touch controller varies
  by production revision — early units use ILI9881C + GT911, units built
  from around October 2025 use an integrated ST7123. M5GFX detects which
  at `M5.begin()` and applies the matching DSI clock, timing, init table
  and touch driver; there is no board-revision build flag and nothing in
  this firmware needs to care.
- PSRAM: **32MB**. Flash: **16MB**.
- Also present and unused by this firmware: camera, microphone, speaker,
  IMU, RTC, RS485, USB-A host port.
- Battery, with an INA226 power monitor rather than the Core2's AXP192.

## Toolchain: three things that aren't like the Core2

**1. The platform is a fork.** Mainline `espressif32` has no ESP32-P4
support at all, so `[env:tab5]` uses
[pioarduino](https://github.com/pioarduino/platform-espressif32), pinned
to release `55.03.35` (Arduino core 3.3.5, ESP-IDF 5.5.1). Pinned, not
floating: P4 support is young enough that adjacent releases have broken
specific boards, and `55.03.35` is the release the Tab5 community settled
on to avoid a MIPI-DSI backlight flicker regression. Newer releases may
well be fine — try one deliberately and *look at the panel*, rather than
drifting onto one silently.

**2. There is no `m5stack-tab5` board definition.** Upstream PlatformIO
doesn't ship one and neither does pioarduino, so the build uses the
generic `esp32-p4-evboard` and overrides what differs: flash size,
partition table (`partitions_16mb_ota.csv`), and — far more importantly —
the Wi-Fi SDIO pins, below.

**3. The toolchain installer is picky about shells.** pioarduino fetches
the RISC-V toolchain via `idf_tools.py`, which refuses to run under
MSys/MinGW with `ERROR: MSys/Mingw is not supported`. On Windows, run
`pio run -e tab5` from PowerShell or cmd, not Git Bash. The failure looks
like `'riscv32-esp-elf-g++' is not recognized`, which is not an obvious
symptom of a shell problem.

## Two platforms in one PlatformIO installation

Having both boards in one project means having two Espressif platforms
installed side by side, and they do not coexist cleanly. Three specific
things happen; all three are understood, reproducible, and worked around.

**The platform name collides.** pioarduino's manifest also calls itself
`espressif32`, so a URL-installed pioarduino claims the unversioned
`~/.platformio/platforms/espressif32` directory. A bare
`platform = espressif32` in the Core2 env then resolves to whichever was
installed last. Building the Tab5 once was enough to silently move the
Core2 build onto Arduino core 3.3.5 — flash went 1,339,069 → 1,545,247
bytes with no warning, off the toolchain a physical unit was validated
against. **Fixed in the repo:** `[env:core2]` pins
`platform = espressif32@7.0.1`, which resolves to the versioned directory
and can't be shadowed. Don't un-pin it.

**The Arduino framework package collides too**, and this one can't be
fixed from `platformio.ini` — both platforms want
`~/.platformio/packages/framework-arduinoespressif32` at different
versions, so each build reinstalls it and evicts the other's. The
observable behaviour, measured over four alternating build cycles:

| Sequence | Result |
|---|---|
| `pio run -e core2` after a Tab5 build | Succeeds, but takes ~45s instead of ~9s (silent framework reinstall) |
| `pio run -e tab5` after a Core2 build | **Fails in ~11s** with `TypeError: ... not 'NoneType'` from `arduino.py`, because `FRAMEWORK_DIR` is `None` |
| `pio run -e tab5` again, immediately | Succeeds |

So: **if a Tab5 build fails immediately after a Core2 build with a
`NoneType` path error, just run it again.** It is not your code. The
structural alternative — giving each board its own `PLATFORMIO_CORE_DIR`
so the two never share a package tree — should work but has not been
tried here, and costs a second full toolchain download.

**`intelhex` can go missing.** Installing the pioarduino platform left
PlatformIO's Python without the `intelhex` module, after which the *Core2*
build fails at `bootloader.bin` with `ModuleNotFoundError: No module named
'intelhex'`. Fix with `python -m pip install intelhex` against whatever
Python `pio system info` reports as its Python Executable — note that's
often the system Python, not `~/.platformio/penv`.

## Wi-Fi: the thing most likely to bite you

The P4 has no radio. `WiFi.h` works anyway — esp-hosted forwards the API
over SDIO to the C6, and `HTTPClient`, `ESPAsyncWebServer`, `ArduinoOTA`
and `ESPmDNS` all sit on top of it unchanged. That's why the app layer
needed no Wi-Fi changes for this board.

But **the Tab5's SDIO GPIOs are not the P4 eval board's**, and since this
env builds against the eval board's definition, its (wrong) defaults are
exactly what you get unless overridden. `src/board/tab5.cpp` calls
`WiFi.setPins()` with the Tab5's actual pins from `board::begin()`, which
`setup()` invokes immediately after `M5.begin()` and before anything
touches the network:

| Signal | GPIO |
|---|---|
| CLK | 12 |
| CMD | 13 |
| D0 | 11 |
| D1 | 10 |
| D2 | 9 |
| D3 | 8 |
| RST | 15 |

Source: [M5Stack's Tab5 Wi-Fi documentation](https://docs.m5stack.com/en/arduino/m5tab5/wifi).

If this is wrong or missing, the failure mode is total and unhelpful:
every Wi-Fi call fails, the device never leaves provisioning mode, and
nothing on serial points at the pins.

If Wi-Fi fails on a device that is otherwise healthy, the other suspect is
the C6's slave firmware — M5Stack publish a
[restore procedure](https://docs.m5stack.com/en/guide/restore_factory/m5tab5_c6_wifi).
Check that before suspecting this project.

**Unverified beyond association:** this firmware also runs a **softAP**
during provisioning (`WIFI_AP_STA`) and calls `WiFi.scanNetworks()` from
the setup page. Both are ordinary ESP32 operations that esp-hosted is
supposed to forward, but neither has been exercised on a P4. The Core2's
provisioning flow needed real-hardware fixes for exactly this kind of
radio-timing behaviour (see `docs/CORE2_HARDWARE.md`); expect to spend
time here.

## Library versions

Tab5 panel support landed in **M5Unified 0.2.17 / M5GFX 0.2.22** — older
versions will not drive this screen. `[env:tab5]` requires `^0.2.17` and
`^0.2.22` respectively and currently resolves to M5Unified 0.2.19 / M5GFX
0.2.26.

`[env:core2]` deliberately stays on its original `M5Unified @ ^0.1.15`
pin. That is the configuration a physical Core2 was validated with, and
adding a second board is not a reason to revalidate the first.

## Memory budget

Real numbers from `pio run -e tab5` (pioarduino 55.03.35, Arduino core
3.3.5, M5Unified 0.2.19, M5GFX 0.2.26, ArduinoJson 6.21.6,
ESPAsyncWebServer 3.12.0, AsyncTCP 3.5.0, QRCode 0.0.1):

| Metric | Value | Confidence |
|---|---|---|
| Flash used | 1,485,826 bytes (22.7% of the 6.25MB OTA slot) | **Measured** — real build output |
| RAM used (static, at boot) | 72,372 bytes (14.1% of 512,000 bytes internal SRAM) | **Measured** — build output. Static/global data only, not runtime heap. PSRAM is separate and not counted here |
| Full-screen canvas | 1280 × 720 × 2 bytes ≈ **1.84MB**, allocated from PSRAM in `Display::begin()` | Arithmetic, not measured. Falls back to direct-to-panel drawing with a serial warning if `createSprite()` fails |
| Parsed aircraft array | `StaticJsonDocument<16384>` for the adsb.lol response (≤10 `AircraftState` records) | Bounded by design; shared with the Core2 |
| Display headroom | 32MB PSRAM against a 1.84MB canvas | Spec sheet |

Note that the P4's internal SRAM (512KB) is much smaller than the ESP32's
reported figure on the Core2, so the *percentage* looks alarming next to
the Core2's 1.9% while being a smaller absolute number. The large
allocations on this board live in PSRAM.

Unlike the Core2, this board has enough PSRAM that TLS headroom is not a
design constraint — but `docs/ARCHITECTURE.md`'s rule still stands: the
gateway path exists for reasons beyond one board's memory, and this
firmware's direct adsb.lol polling is deliberately kept identical across
boards rather than being tuned per-board.

## Battery monitoring

`src/app/battery_monitor.cpp` is shared and unchanged: it asks
`M5.Power.getBatteryLevel()` / `getBatteryVoltage()` / `isCharging()` and
treats a negative level as "unknown", which the header renders as a `?`
inside the battery outline. M5Unified is expected to route those to the
Tab5's INA226 the same way it routes them to the Core2's AXP192.

**Unverified.** If the battery pill shows `?` on real hardware, that call
path is where to look — the display code is doing exactly what it's told.

## Antenna

The Tab5 has both an internal antenna and an external SMA connector,
selected by an IO-expander pin (E1.P0: low = internal, high = external).
M5Unified's initialisation is expected to default to internal, and this
firmware does not touch it. If range is poor with no external antenna
fitted, that's the first thing to check.

## What to check first when you get one

In rough order of "most likely to be wrong":

1. **Does it boot and light the panel at all?** Watch serial (USB CDC —
   `-DARDUINO_USB_CDC_ON_BOOT=1` is set for this reason).
2. **`PANEL SIZE MISMATCH` on serial?** `Display::begin()` compares the
   panel's reported size against the layout profile and says so loudly.
   If it fires, the rotation in `board::beginDisplay()` is wrong.
3. **Does the full-screen canvas allocate?** A `full-screen canvas alloc
   failed` warning means it fell back to direct drawing; the screen will
   work but wipe visibly on each redraw.
4. **Does Wi-Fi associate?** If not, see the Wi-Fi section above — pins
   first, C6 firmware second.
5. **Does the provisioning softAP appear, and does the network scan on the
   setup page work?** The least-tested path on this board.
6. **Do the three tabs respond to taps**, including near the column
   boundaries at x=427 and x=854?
7. **Does the layout actually look right?** Every number in
   `ui_layout_tab5.h` is derived from font metrics rather than observed.
   Expect to adjust; that file is the only place that should need it.
8. **Does OTA complete?** `[env:tab5-ota]` exists and the partition table
   has two 6.25MB slots, but espota over esp-hosted has not been tried.

## Wi-Fi security modes

Same caveat as the Core2: WPA2-Personal is the assumed common case. WPA3
and transition-mode behaviour on a C6 running esp-hosted has not been
tested here.
