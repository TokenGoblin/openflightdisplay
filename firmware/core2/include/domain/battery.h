#pragma once

#include <cstdint>

namespace ofd {

// Normalized battery state — read once every N seconds by the
// BatteryMonitor and cached in AppContext. Both the Core2 display
// and the web API consume this single model.
struct BatteryState {
  bool valid = false;       // false if the PMIC couldn't be read
  uint8_t percent = 0;     // 0–100 (derived from voltage curve)
  float voltage = 0.0f;    // raw voltage, e.g. 4.03
  bool charging = false;   // actively charging
  bool externalPower = false;  // USB or external power connected
  uint32_t lastReadMs = 0;     // millis() when last polled
};

}  // namespace ofd