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

// Pointer to the global AppContext, set by main.cpp in setup().
// Used by the display module to read cached battery state without
// introducing a circular header dependency.
extern AppContext* s_ctx;

class Display {
 public:
  void begin();

  void renderBoot();
  void renderProvisioning(const char* apName);
  void renderPairingReady(const char* ipAddress, const char* pairingCode);
  void renderSingleAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds);
  // `ipAddress` is shown (when non-empty) beneath the status message so
  // a user who needs to re-enter it in the PWA (e.g. after losing setup
  // progress -- verified needed on real hardware, where a mobile browser
  // tab reset mid-wizard left no way to recover the IP once the Core2
  // had already moved on to "configuration required") always has it
  // on-screen, not just during the one-time pairing-QR screen.
  void renderStatus(StatusMessage message, const char* ipAddress = "");
  void renderIdleClock(const char* timeHhMm, bool wifiConnected, bool gatewayConnected);

  // OTA progress screen — percentage 0-100, complete=true for success screen.
  void renderOtaProgress(uint8_t percent, bool complete, const char* status);

  // Call once per loop() iteration.
  void update();
};

}  // namespace ofd::app
