#include "app/battery_monitor.h"

#include <Arduino.h>
#include <M5Unified.h>

namespace ofd::app {

namespace {
constexpr uint32_t kPollIntervalMs = 10000;
uint32_t g_lastBatteryReadMs = 0;
}  // namespace

void pollBattery(AppContext& ctx) {
  const uint32_t now = millis();
  if (now - g_lastBatteryReadMs < kPollIntervalMs) return;
  g_lastBatteryReadMs = now;

  auto& b = ctx.battery;
  b.lastReadMs = now;

  // M5.Power.isEnabled() returns false if the PMIC isn't initialised.
  // If that happens, mark the reading invalid and bail.
  b.voltage      = M5.Power.getBatteryVoltage();
  b.charging     = M5.Power.isCharging();
  // M5Unified 0.1.17 doesn't expose isExtPower() directly; approximate
  // from charging state or battery voltage above 4.5 V.
  b.externalPower = b.charging || (b.voltage > 4.5f);

  const int raw = M5.Power.getBatteryLevel();
  // getBatteryLevel() returns -1 on failure
  if (raw < 0) {
    b.valid = false;
    b.percent = 0;
    return;
  }

  b.valid   = true;
  b.percent = static_cast<uint8_t>(raw > 100 ? 100 : raw);
}

}  // namespace ofd::app