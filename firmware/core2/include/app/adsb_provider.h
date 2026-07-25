#pragma once

#include "app/app_context.h"

namespace ofd::app {

// Direct HTTPS poller for adsb.lol (or compatible tar1090-style API).
// Runs on a dedicated FreeRTOS task so TLS handshakes and JSON parsing
// never block the UI loop.
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