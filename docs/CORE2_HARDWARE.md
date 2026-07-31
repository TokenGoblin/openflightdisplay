# Core2 Hardware Notes

**A physical M5Stack Core2 was connected and used for real end-to-end testing** (full provisioning, pairing, live aircraft data from adsb.lol, gateway-down/recovery). It ran stably for the duration of that testing with no crashes remaining after the bugs below were fixed. What's still not done: a multi-day continuous-operation soak test to check for slow heap leaks, and a Wi-Fi-router-power-loss test (see `docs/TEST_PLAN.md`'s manual hardware validation checklist for exactly what's covered and what isn't).

## Target hardware (confirmed on the actual unit used for testing)

- MCU: **ESP32-D0WDQ6 revision v3.0** (confirmed via `esptool.py`), dual-core Xtensa LX6, 240MHz
- Display: 320×240 capacitive touchscreen (ILI9342C-family controller, driven via LovyanGFX/M5Unified)
- Flash: **16MB, confirmed**
- PSRAM: **confirmed absent** on this specific unit (esptool's chip-feature detection listed WiFi/BT/Dual Core/240MHz/VRef-calibration but no PSRAM) — this validates the caution below about not assuming TLS/JSON-parsing headroom from PSRAM. Other Core2 units/batches may differ; don't assume this generalizes.
- RTC, speaker, vibration motor, microSD slot (SD card is optional in this design, not required for Phase 1)
- Wi-Fi 802.11 b/g/n; BLE (unused in Phase 1)
- USB-serial: this unit uses a CH9102 adapter. The auto-reset sequence (RTS/DTR toggling GPIO0/EN) that's supposed to bring the board out of the bootloader after flashing did not reliably work over this cable/port combination -- a manual power-cycle was needed after every single flash in this session. If a flash "succeeds" but the board then shows "waiting for download" on serial instead of booting, this is why; try a different cable/port, or just power-cycle manually.

## Why this matters for the architecture

TLS (HTTPS) client connections on plain ESP32 (no PSRAM) typically need on the order of tens of KB of heap for the mbedTLS handshake and session buffers, on top of whatever the application and networking stack are already using. Without confirmed PSRAM, that's a real risk for a device meant to run continuously for weeks. This is the concrete reason `docs/ARCHITECTURE.md` puts all HTTPS provider polling in the gateway and keeps the Core2 talking plain WebSocket/HTTP on the LAN only.

## Memory budget

Real numbers from `pio run -e core2` (espressif32 platform 7.0.1, M5Unified 0.1.17, M5GFX 0.2.26, ArduinoJson 6.21.6, ESPAsyncWebServer 3.11.2, AsyncTCP 3.5.0, QRCode 0.0.1), default partition table:

| Metric | Value | Confidence |
|---|---|---|
| Flash used | 1,347,117 bytes (20.6% of 6,553,600 available to the app partition) | **Measured** — real build output |
| RAM used (static, at boot) | 82,812 bytes (1.8% of 4,521,984 bytes) | **Measured** — real build output. This is static/global data only, not a runtime heap-usage measurement |
| Parsed aircraft array | Fixed-capacity `ArduinoJson` `StaticJsonDocument<16384>`, parsed **from the socket with a field filter** (13 of adsb.lol's ~51 fields) | **Measured** — filtering cuts a real 114-aircraft response from 66,082 to 20,149 bytes (69.5%), roughly tripling how much airspace fits. Streaming means peak heap no longer includes a full copy of the payload |
| Query radius | Clamped to 80 NM regardless of configured radius | **Measured** — 13.7KB/28 aircraft at 50 NM, 77.3KB/133 at 270 NM. Past ~60 NM the response outgrew the buffer and the display went permanently blank with no diagnostic; the nearest aircraft is the same either way |
| Config JSON documents | `StaticJsonDocument<768>` for device config (×2, parse + serialize), `StaticJsonDocument<160>` for Wi-Fi credentials/pairing token | Bounded by design |
| Display buffers | A small persistent header sprite (320×31, ~19.8KB) plus direct-to-panel region-clear redraws for the body — not a full 320×240 16-bit framebuffer (~150KB) — to reduce heap pressure. See `docs/DISPLAY_UI.md`'s "Sprite and buffering strategy" for the full reasoning | Compiles; actual runtime heap headroom during rendering (esp. concurrent with an HTTPS poll) is unmeasured |
| OTA partition | Two 6.25MB slots (ota_0/ota_1) from the board definition's `default_16MB.csv`; the figures above are against one slot. `[env:core2-ota]` uploads via espota | **Measured** — build output |

The flash/RAM figures above cover the standalone direct-adsb.lol-polling firmware with OTA, battery monitoring and flight tracking. The board layer added to support a second board is compile-time and cost +780 bytes flash / +344 bytes static RAM.

An audit pass then removed **3,736 bytes of static RAM**: `domain/protocol.cpp` (a parser for the gateway's WebSocket frames, plus its 4,136-byte `StaticJsonDocument`) had been unreachable since the firmware started polling adsb.lol directly, and was carried in `.bss` on every boot for code nothing called. It was the second-largest static allocation in the binary, after the response buffer itself. Flash grew ~2.2KB in the same pass, from the response filter and the redraw-suppression logic — a deliberate trade of flash (of which there are 5MB spare) for RAM and bandwidth (of which there are not).

**Runtime behavior since this table was written:** the device ran continuously through an extended real testing session (provisioning, pairing, live aircraft data, deliberate gateway restarts, WebSocket reconnects) with no crashes remaining after fixing two real bugs found in the process -- see `docs/TEST_PLAN.md` for the full list, but the significant one for this table specifically was a genuine stack overflow in the WebSocket client (loopTask's default 8KB stack wasn't enough for that library's call depth once real aircraft data started flowing), fixed by moving the WS client onto its own dedicated 16KB-stack FreeRTOS task. **Still not done:** logging `ESP.getFreeHeap()` periodically over a multi-day unattended run to check for a slow leak -- the numbers above, plus a few hours of stable interactive operation, are not the same as a long-term guarantee.

## Display library choice

M5Unified (which wraps LovyanGFX for the Core2's specific panel) is used directly for Phase 1's single-aircraft screen and status states — no LVGL. LVGL's memory and complexity cost isn't justified for a handful of static-ish screens; it would be worth revisiting if Phase 2/3 introduces genuinely complex, deeply nested, animated UI (e.g., a scrollable flight board with many rows).

## Touch interaction

Phase 1 only needs a "tap anywhere to cycle status/diagnostics" gesture (no aircraft list yet to tap into). Full swipe/long-press/quick-menu interactions are Phase 2+, once there's more than one screen to navigate between.

## Wi-Fi security modes

WPA2-Personal is the only mode exercised in this session's design (and the most common home-network case). WPA3 and WPA2/WPA3-transition mode support depend on the specific ESP32 SiP revision and IDF/Arduino-core version in use — this needs to be confirmed against the real hardware/toolchain combination the user ends up building with, not assumed.
