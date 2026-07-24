#include "app/gateway_client.h"

#include <WebSocketsClient.h>

#include <cstring>

#include "domain/protocol.h"
#include "domain/ranking.h"

namespace ofd::app {

namespace {
WebSocketsClient g_ws;

// GatewayClient::begin() is only ever called once, against the single
// global AppContext (see main.cpp). Storing a raw pointer here (rather
// than capturing `ctx` in the onEvent lambda below) means the callback
// can be a capture-less lambda, which is guaranteed to convert to
// whatever function-pointer-or-std::function type onEvent() expects --
// some versions of Links2004/WebSockets take a plain C function pointer,
// which a *capturing* lambda cannot bind to. This sidesteps needing to
// know which one without a compiler to check.
AppContext* g_ctx = nullptr;

// Splits "ws://host:port/path" (the only scheme the Core2 ever dials --
// see docs/ARCHITECTURE.md on why wss:// isn't attempted from firmware)
// into its parts. Returns false on anything else.
bool parseWsUrl(const char* url, char* hostOut, size_t hostOutLen, uint16_t& portOut, char* pathOut,
                 size_t pathOutLen) {
  if (std::strncmp(url, "ws://", 5) != 0) return false;
  const char* rest = url + 5;
  const char* slash = std::strchr(rest, '/');
  const char* hostPortEnd = slash != nullptr ? slash : rest + std::strlen(rest);

  const char* colon = nullptr;
  for (const char* p = rest; p < hostPortEnd; p++) {
    if (*p == ':') colon = p;
  }

  const char* hostEnd = colon != nullptr ? colon : hostPortEnd;
  const size_t hostLen = static_cast<size_t>(hostEnd - rest);
  if (hostLen == 0 || hostLen >= hostOutLen) return false;
  std::memcpy(hostOut, rest, hostLen);
  hostOut[hostLen] = '\0';

  portOut = 80;
  if (colon != nullptr) {
    uint32_t port = 0;
    for (const char* p = colon + 1; p < hostPortEnd; p++) {
      if (*p < '0' || *p > '9') return false;
      port = port * 10 + static_cast<uint32_t>(*p - '0');
    }
    portOut = static_cast<uint16_t>(port);
  }

  const char* path = slash != nullptr ? slash : "/";
  if (std::strlen(path) >= pathOutLen) return false;
  std::strcpy(pathOut, path);
  return true;
}

void handleServerMessage(AppContext& ctx, const uint8_t* payload, size_t len) {
  ofd::ParsedServerMessage msg;
  char error[64] = {0};
  if (!ofd::parseServerMessage(reinterpret_cast<const char*>(payload), len, msg, error, sizeof(error))) {
    // Malformed/unrecognized frame -- ignore it (don't crash, don't
    // misrender), per docs/PROTOCOL.md.
    return;
  }

  ctx.lastServerMessageAtMs = millis();

  switch (msg.type) {
    case ofd::ServerMessageType::Heartbeat:
      break;
    case ofd::ServerMessageType::ProviderStatus:
      ctx.providerHealth = msg.providerHealth;
      std::strncpy(ctx.providerStatusMessage, msg.statusMessage, sizeof(ctx.providerStatusMessage) - 1);
      break;
    case ofd::ServerMessageType::AircraftUpdate: {
      ofd::AircraftList ranked = msg.aircraft;
      if (ctx.hasConfig && ctx.config.hasMonitoringArea) {
        // Defensive re-ranking against our own configured area -- see
        // docs/ARCHITECTURE.md ("Firmware still implements its own
        // ranking/staleness logic").
        ranked = ofd::rankNearest(msg.aircraft, ctx.config.monitoringArea);
      }
      ctx.latestAircraft = ranked;
      ctx.hasLatestAircraft = true;
      ctx.lastAircraftUpdateAtMs = millis();
      break;
    }
    case ofd::ServerMessageType::Unknown:
      break;
  }
}
}  // namespace

void GatewayClient::begin(AppContext& ctx) {
  g_ctx = &ctx;

  if (!ctx.hasConfig || !ctx.config.hasGatewayUrl || !ctx.hasPairingToken) {
    ctx.gatewayState = GatewayConnectionState::Unconfigured;
    return;
  }

  char host[64];
  uint16_t port;
  char basePath[96];
  if (!parseWsUrl(ctx.config.gatewayUrl, host, sizeof(host), port, basePath, sizeof(basePath))) {
    ctx.gatewayState = GatewayConnectionState::Unconfigured;
    return;
  }

  char path[192];
  std::snprintf(path, sizeof(path), "%s?deviceId=%s&token=%s", basePath, ctx.deviceId, ctx.pairingToken);

  g_ws.begin(host, port, path);
  g_ws.setReconnectInterval(1000);  // library handles backoff internally; see docs/PROTOCOL.md for policy intent
  g_ws.onEvent([](WStype_t type, uint8_t* payload, size_t len) {
    if (g_ctx == nullptr) return;
    AppContext& ctx = *g_ctx;
    switch (type) {
      case WStype_CONNECTED: {
        ctx.gatewayState = GatewayConnectionState::Connected;
        ctx.lastServerMessageAtMs = millis();
        char hello[192];
        const size_t helloLen = ofd::buildHelloMessage(ctx.deviceId, hello, sizeof(hello));
        if (helloLen > 0) g_ws.sendTXT(hello, helloLen);
        break;
      }
      case WStype_DISCONNECTED:
        ctx.gatewayState = GatewayConnectionState::Disconnected;
        break;
      case WStype_TEXT:
        handleServerMessage(ctx, payload, len);
        break;
      default:
        break;
    }
  });

  ctx.gatewayState = GatewayConnectionState::Connecting;
}

void GatewayClient::loop() { g_ws.loop(); }

}  // namespace ofd::app
