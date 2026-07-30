#include "board/board.h"

#include <M5Unified.h>
#include <WiFi.h>

#include "app/ui_layout.h"

// M5Stack Tab5 (ESP32-P4, 1280x720 MIPI-DSI, no on-die radio).
// See docs/TAB5_HARDWARE.md -- NONE of this has been run on a physical
// Tab5. It is written against M5Stack's published pinout and the Arduino
// core's ESP32-P4 Wi-Fi support, and it compiles, which is a much weaker
// claim than the one src/board/core2.cpp gets to make.

namespace ofd::board {

namespace {

// The ESP32-P4 has no radio of its own. Wi-Fi is an ESP32-C6
// co-processor on the far end of an SDIO bus, reached through
// esp-hosted/esp_wifi_remote and presented to sketch code as the
// ordinary WiFi object -- WiFi.begin(), HTTPClient and
// ESPAsyncWebServer all work unchanged above this line.
//
// These GPIOs are the Tab5's, and they are NOT the ESP32-P4 evaluation
// board's, whose numbers the Arduino core compiles in as its defaults.
// Since [env:tab5] builds against the eval board's definition (no
// upstream PlatformIO board file exists for the Tab5 -- see
// platformio.ini), those wrong defaults are exactly what you get unless
// setPins() overrides them. The failure mode is total: every Wi-Fi call
// fails, the device never leaves provisioning mode, and nothing on
// serial points at the pins.
//
// Source: M5Stack's Tab5 Wi-Fi documentation,
// https://docs.m5stack.com/en/arduino/m5tab5/wifi
constexpr int8_t kC6SdioClk = 12;
constexpr int8_t kC6SdioCmd = 13;
constexpr int8_t kC6SdioD0 = 11;
constexpr int8_t kC6SdioD1 = 10;
constexpr int8_t kC6SdioD2 = 9;
constexpr int8_t kC6SdioD3 = 8;
constexpr int8_t kC6SdioRst = 15;

}  // namespace

void begin() {
  WiFi.setPins(kC6SdioClk, kC6SdioCmd, kC6SdioD0, kC6SdioD1, kC6SdioD2, kC6SdioD3, kC6SdioRst);
}

void beginDisplay() {
  M5.Display.setRotation(1);
  M5.Display.setBrightness(200);
}

app::DetailPage pollPageRequest(app::DetailPage current) {
  // No buttons under this panel, so the tab bar drawn at the bottom of
  // every operational screen is the actual control. Hit-testing it here
  // rather than in main.cpp keeps "how a page gets selected" a board
  // concern, which is the whole point of this layer.
  //
  // wasClicked(), not wasPressed(): a click is a press and release
  // inside the same area, so dragging a finger across the bar or resting
  // one on it while picking the tablet up doesn't count as a tap.
  if (M5.Touch.getCount() == 0) return current;

  const auto touch = M5.Touch.getDetail(0);
  if (!touch.wasClicked()) return current;
  if (touch.y < app::layout::kTabBarY) return current;

  const app::DetailPage pages[3] = {app::DetailPage::Flight, app::DetailPage::Detail,
                                    app::DetailPage::System};
  for (int i = 0; i < 3; i++) {
    if (touch.x >= app::layout::kTabBarColBoundaries[i] && touch.x < app::layout::kTabBarColBoundaries[i + 1]) {
      return pages[i];
    }
  }
  return current;
}

}  // namespace ofd::board
