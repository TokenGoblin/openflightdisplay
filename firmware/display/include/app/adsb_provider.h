#pragma once

#include "app/app_context.h"

namespace ofd::app {

// Direct HTTPS poller for adsb.lol (or compatible tar1090-style API).
// Runs on a dedicated FreeRTOS task so TLS handshakes and JSON parsing
// never block the UI loop.
//
// The one task serves two jobs on independent timers:
//
//   1. Nearest aircraft, from a geographic query around the configured
//      monitoring area, on a fixed interval.
//   2. A tracked flight, from a direct callsign lookup, on an interval
//      that tightens as the flight approaches its destination
//      (domain/flight_tracking.h's pollIntervalMsFor).
//
// Deliberately one task rather than two: a second one would cost another
// 16KB stack and a second concurrent mbedTLS session, which is real money
// on a board with no PSRAM (docs/CORE2_HARDWARE.md). Sharing one
// WiFiClientSecure also means the TLS session is negotiated once and
// reused across both request types.
//
// No API key required for adsb.lol as of this writing (see docs/
// DATA_SOURCE_EVALUATION.md). Swapping providers is a one-line URL
// change in the .cpp file.
class AdsbProvider {
 public:
  // Starts the background polling task. Safe to call multiple times
  // (idempotent — only one task is ever created).
  void begin(AppContext& ctx);

 private:
  bool m_started = false;
};

}  // namespace ofd::app