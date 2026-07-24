# Core2 Hardware Notes

**No physical M5Stack Core2 was available during this implementation session.** Every number and claim below is either from the publicly documented M5Stack Core2 hardware spec or an engineering estimate, and is marked accordingly. Re-verify against real hardware before treating any of this as ground truth — see `docs/TEST_PLAN.md`'s manual hardware validation checklist.

## Target hardware (per M5Stack's published spec — not independently re-verified here)

- MCU: ESP32 (dual-core Xtensa LX6)
- Display: 320×240 capacitive touchscreen (ILI9342C-family controller, driven via LovyanGFX/M5Unified)
- Flash: 16MB
- PSRAM: Core2 variants have shipped with and without PSRAM depending on revision/batch — **this is the single most important thing to confirm on the actual unit before assuming any RAM headroom for TLS or JSON parsing.** Do not assume PSRAM is present.
- RTC, speaker, vibration motor, microSD slot (SD card is optional in this design, not required for Phase 1)
- Wi-Fi 802.11 b/g/n; BLE (unused in Phase 1)

## Why this matters for the architecture

TLS (HTTPS) client connections on plain ESP32 (no PSRAM) typically need on the order of tens of KB of heap for the mbedTLS handshake and session buffers, on top of whatever the application and networking stack are already using. Without confirmed PSRAM, that's a real risk for a device meant to run continuously for weeks. This is the concrete reason `docs/ARCHITECTURE.md` puts all HTTPS provider polling in the gateway and keeps the Core2 talking plain WebSocket/HTTP on the LAN only.

## Estimated memory budget (Phase 1 firmware — unverified without a real build)

| Component | Estimate | Confidence |
|---|---|---|
| Firmware binary (Arduino core + M5Unified/LovyanGFX + ESPAsyncWebServer/AsyncTCP + ArduinoJson + WS client + QRCode lib) | ~1.2-1.6MB flash | Rough estimate based on typical library sizes; confirm with `pio run -e core2` binary size output once buildable |
| LittleFS partition for config | 64-256KB reserved | Config JSON itself is a few KB; partition sized generously for headroom and future OTA-adjacent needs |
| Network buffers (WS client + AsyncWebServer) | A few KB per connection | Standard for these libraries |
| Parsed aircraft array | Fixed-capacity `ArduinoJson` document sized for ≤10 `AircraftState` records (bounded per `docs/PROTOCOL.md`) | Bounded by design, not by measurement yet |
| Display buffers | Depends on LovyanGFX's chosen buffering mode (full-frame vs. partial/sprite) — Phase 1 uses partial-region redraws, not a full 320×240 16-bit framebuffer (~150KB), to reduce heap pressure | Needs confirmation against real free-heap readings |
| OTA partition | Not used in Phase 1 (OTA is Phase 5) — default single-app partition table for now | — |

**Action item for whoever has hardware:** build with `pio run -e core2`, note the reported flash/RAM usage, then run with `ESP.getFreeHeap()` logged periodically over at least a few hours, and update this table with real numbers.

## Display library choice

M5Unified (which wraps LovyanGFX for the Core2's specific panel) is used directly for Phase 1's single-aircraft screen and status states — no LVGL. LVGL's memory and complexity cost isn't justified for a handful of static-ish screens; it would be worth revisiting if Phase 2/3 introduces genuinely complex, deeply nested, animated UI (e.g., a scrollable flight board with many rows).

## Touch interaction

Phase 1 only needs a "tap anywhere to cycle status/diagnostics" gesture (no aircraft list yet to tap into). Full swipe/long-press/quick-menu interactions are Phase 2+, once there's more than one screen to navigate between.

## Wi-Fi security modes

WPA2-Personal is the only mode exercised in this session's design (and the most common home-network case). WPA3 and WPA2/WPA3-transition mode support depend on the specific ESP32 SiP revision and IDF/Arduino-core version in use — this needs to be confirmed against the real hardware/toolchain combination the user ends up building with, not assumed.
