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
  //
  // Verified needed on real hardware: pumping the WebSocket client from
  // Arduino's own loop() (on loopTask, an 8KB-stack task) crashed with
  // "Stack canary watchpoint triggered (loopTask)" the moment real
  // aircraft data arrived -- Links2004/WebSockets' frame-parsing design
  // is inherently deep (a chain of nested "wait for N bytes, then
  // continue" callbacks, each wrapped in several std::function/lambda
  // indirection frames), not a bug on either side, just more stack than
  // an 8KB task can offer. begin() now spawns a dedicated FreeRTOS task
  // with a generous stack to run the WS client's loop() on instead.
  void begin(AppContext& ctx);
};

}  // namespace ofd::app
