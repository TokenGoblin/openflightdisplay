#include "board/board.h"

#include <M5Unified.h>

// M5Stack Core2 (ESP32-D0WDQ6, 320x240, on-die Wi-Fi radio).
// See docs/CORE2_HARDWARE.md -- every claim in this file is backed by a
// physical unit that was flashed and run end-to-end.

namespace ofd::board {

void begin() {
  // Nothing to do. This board's Wi-Fi radio is on the same die as the
  // CPU, so WiFi.begin() works the moment M5.begin() has returned. The
  // hook exists for boards where that isn't true -- see src/board/tab5.cpp.
}

void beginDisplay() {
  // Rotation 1 is landscape with the three buttons along the bottom
  // edge, directly under the tab bar's three columns.
  M5.Display.setRotation(1);
  M5.Display.setBrightness(200);
}

app::DetailPage pollPageRequest(app::DetailPage current) {
  // Each button jumps straight to one page rather than cycling
  // prev/next, so a given physical button always means the same thing --
  // see docs/DISPLAY_UI.md "Page navigation". M5.update() (called from
  // Display::update() at the top of loop()) is what refreshes these.
  if (M5.BtnA.wasPressed()) return app::DetailPage::Flight;
  if (M5.BtnB.wasPressed()) return app::DetailPage::Detail;
  if (M5.BtnC.wasPressed()) return app::DetailPage::System;
  return current;
}

}  // namespace ofd::board
