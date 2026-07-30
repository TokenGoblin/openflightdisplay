#pragma once

#include <ESPAsyncWebServer.h>

#include "app/app_context.h"

namespace ofd::app {

// Registers the Core2's own local HTTP API (POST /pair, GET
// /api/v1/status, GET/PUT /api/v1/config) on the shared AsyncWebServer
// instance -- see docs/PROTOCOL.md for the exact request/response
// shapes. Config reads/writes require a valid pairing-token bearer
// header; /pair and /status are intentionally open (see
// docs/PROVISIONING.md and docs/SECURITY_AND_PRIVACY.md for why).
void registerPairingRoutes(AsyncWebServer& server, AppContext& ctx);

}  // namespace ofd::app
