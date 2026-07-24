#pragma once

#include "app/app_context.h"

namespace ofd::app {

// Wraps the outbound WebSocket connection to the gateway's
// /ws/v1/aircraft feed (docs/PROTOCOL.md). The Core2 is always the WS
// *client* here -- the gateway is the server -- so this never listens
// for inbound connections.
class GatewayClient {
 public:
  // Parses ctx.config.gatewayUrl (a ws://host:port/path URL), appends
  // the deviceId/pairingToken query params, and opens the connection.
  // Safe to call again to reconnect after a config change.
  void begin(AppContext& ctx);

  // Must be called every loop() iteration.
  void loop();
};

}  // namespace ofd::app
