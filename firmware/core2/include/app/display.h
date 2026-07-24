#pragma once

#include "app/app_context.h"

namespace ofd::app {

// Explicit status states shown instead of any indefinite spinner/loading
// state -- see docs/PRODUCT_REQUIREMENTS.md and docs/ARCHITECTURE.md.
enum class StatusMessage {
  WaitingForFirstData,
  NoMatchingAircraft,
  DataSourceUnavailable,
  WifiDisconnected,
  ConfigurationRequired,
  DataIsStale,
};

class Display {
 public:
  void begin();

  void renderBoot();
  void renderProvisioning(const char* apName);
  void renderPairingReady(const char* ipAddress, const char* pairingCode);
  void renderSingleAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds);
  void renderStatus(StatusMessage message);
  void renderIdleClock(const char* timeHhMm, bool wifiConnected, bool gatewayConnected);

  // Call once per loop() iteration; handles the touch-to-toggle-diagnostics
  // gesture (docs/CORE2_HARDWARE.md's minimal Phase 1 touch interaction).
  void update();
};

}  // namespace ofd::app
