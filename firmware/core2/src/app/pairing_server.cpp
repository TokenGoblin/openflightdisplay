#include "app/pairing_server.h"

#include <ArduinoJson.h>
#include <esp_system.h>

#include <cstdio>
#include <cstring>

namespace ofd::app {

namespace {

// ESPAsyncWebServer delivers request bodies via a chunked callback.
// Phase 1 bodies are small (<1KB, see docs/PROTOCOL.md) and this device
// only ever serves one LAN client at a time in practice, so a single
// static staging buffer keyed by nothing more than "the most recent
// request" is an acceptable simplification here -- documented rather
// than silently relied upon.
constexpr size_t kBodyBufferCapacity = 1024;
char g_bodyBuffer[kBodyBufferCapacity];

void generatePairingToken(char* out, size_t outLen) {
  static const char kHex[] = "0123456789abcdef";
  size_t i = 0;
  while (i + 8 <= outLen - 1) {
    const uint32_t r = esp_random();
    for (int b = 0; b < 8 && i < outLen - 1; b++, i++) {
      out[i] = kHex[(r >> (b * 4)) & 0xF];
    }
  }
  out[i] = '\0';
}

bool checkBearerToken(AsyncWebServerRequest* request, const AppContext& ctx) {
  if (!ctx.hasPairingToken) return false;
  if (!request->hasHeader("Authorization")) return false;
  const String header = request->getHeader("Authorization")->value();
  if (!header.startsWith("Bearer ")) return false;
  const String token = header.substring(7);
  return token.equals(ctx.pairingToken);
}

void sendJsonError(AsyncWebServerRequest* request, int code, const char* error) {
  StaticJsonDocument<128> doc;
  doc["schemaVersion"] = 1;
  doc["error"] = error;
  char buf[128];
  const size_t len = serializeJson(doc, buf, sizeof(buf));
  request->send(code, "application/json", String(buf, len));
}

const char* wifiStateToString(WifiState s) {
  switch (s) {
    case WifiState::Connected:
      return "connected";
    case WifiState::Provisioning:
      return "provisioning";
    default:
      return "disconnected";
  }
}

const char* gatewayStateToString(GatewayConnectionState s) {
  switch (s) {
    case GatewayConnectionState::Connected:
      return "connected";
    case GatewayConnectionState::Connecting:
      return "connecting";
    case GatewayConnectionState::Disconnected:
      return "disconnected";
    default:
      return "unconfigured";
  }
}

}  // namespace

void registerPairingRoutes(AsyncWebServer& server, AppContext& ctx) {
  // NOTE (unverified without hardware): whether ESPAsyncWebServer invokes
  // the onBody callback at all for a genuinely empty (Content-Length: 0)
  // POST varies by version. A deliberate choice was made NOT to also
  // handle that case in the onRequest callback below, since doing so
  // risks a double request->send() (undefined behavior in this framework)
  // if onBody *does* still fire with total==0. Worst case today: a
  // malformed empty-body request hangs until the client's own timeout,
  // rather than crashing. Revisit once this is buildable against the
  // real library.
  server.on(
      "/pair", HTTP_POST, [](AsyncWebServerRequest* request) { /* response sent from onBody below */ }, nullptr,
      [&ctx](AsyncWebServerRequest* request, uint8_t* data, size_t len, size_t index, size_t total) {
        if (index == 0) {
          if (total >= kBodyBufferCapacity) {
            sendJsonError(request, 400, "invalid_request");
            return;
          }
        }
        std::memcpy(g_bodyBuffer + index, data, len);
        if (index + len < total) return;  // wait for the rest of the body
        g_bodyBuffer[total] = '\0';

        StaticJsonDocument<192> doc;
        if (deserializeJson(doc, g_bodyBuffer, total) || (doc["schemaVersion"] | -1) != 1) {
          sendJsonError(request, 400, "invalid_request");
          return;
        }
        const char* code = doc["code"] | "";
        if (!ctx.pairingCodeManager.tryClaim(code, millis())) {
          sendJsonError(request, 401, "invalid_or_expired_code");
          return;
        }

        char token[40];
        generatePairingToken(token, sizeof(token));
        std::strcpy(ctx.pairingToken, token);
        ctx.hasPairingToken = true;
        ctx.configStore.savePairingToken(token);

        StaticJsonDocument<192> resp;
        resp["schemaVersion"] = 1;
        resp["pairingToken"] = token;
        resp["deviceId"] = ctx.deviceId;
        char buf[192];
        const size_t respLen = serializeJson(resp, buf, sizeof(buf));
        request->send(200, "application/json", String(buf, respLen));
      });

  server.on("/api/v1/status", HTTP_GET, [&ctx](AsyncWebServerRequest* request) {
    StaticJsonDocument<256> doc;
    doc["schemaVersion"] = 1;
    doc["deviceId"] = ctx.deviceId;
    doc["firmwareVersion"] = ctx.firmwareVersion;
    doc["wifiState"] = wifiStateToString(ctx.wifiState);
    doc["gatewayConnectionState"] = gatewayStateToString(ctx.gatewayState);
    if (ctx.hasLatestAircraft) {
      doc["lastAircraftUpdateAgeSeconds"] = (millis() - ctx.lastAircraftUpdateAtMs) / 1000;
    }
    doc["freeHeapBytes"] = ESP.getFreeHeap();
    char buf[256];
    const size_t len = serializeJson(doc, buf, sizeof(buf));
    request->send(200, "application/json", String(buf, len));
  });

  server.on("/api/v1/config", HTTP_GET, [&ctx](AsyncWebServerRequest* request) {
    if (!checkBearerToken(request, ctx)) {
      sendJsonError(request, 401, "invalid_or_missing_pairing_token");
      return;
    }
    if (!ctx.hasConfig) {
      sendJsonError(request, 404, "no_config");
      return;
    }
    char buf[512];
    const size_t len = serializeDeviceConfig(ctx.config, buf, sizeof(buf));
    request->send(200, "application/json", String(buf, len));
  });

  server.on(
      "/api/v1/config", HTTP_PUT, [](AsyncWebServerRequest* request) {}, nullptr,
      [&ctx](AsyncWebServerRequest* request, uint8_t* data, size_t len, size_t index, size_t total) {
        if (!checkBearerToken(request, ctx)) {
          sendJsonError(request, 401, "invalid_or_missing_pairing_token");
          return;
        }
        if (index == 0 && total >= kBodyBufferCapacity) {
          sendJsonError(request, 400, "invalid_config");
          return;
        }
        std::memcpy(g_bodyBuffer + index, data, len);
        if (index + len < total) return;
        g_bodyBuffer[total] = '\0';

        // The PUT body is the {schemaVersion, config: {...}} wrapper from
        // docs/PROTOCOL.md, not the bare config object -- unwrap it before
        // handing the inner object to the domain-layer validator, which
        // expects a bare config JSON (matching how it's unit-tested).
        StaticJsonDocument<kBodyBufferCapacity> wrapper;
        if (deserializeJson(wrapper, g_bodyBuffer, total) || (wrapper["schemaVersion"] | -1) != 1 ||
            !wrapper.containsKey("config")) {
          sendJsonError(request, 400, "invalid_config");
          return;
        }
        char configJson[kBodyBufferCapacity];
        const size_t configLen = serializeJson(wrapper["config"], configJson, sizeof(configJson));

        DeviceConfig parsed;
        char error[64] = {0};
        if (configLen == 0 || !parseAndValidateDeviceConfig(configJson, configLen, parsed, error, sizeof(error))) {
          sendJsonError(request, 400, "invalid_config");
          return;
        }

        ctx.config = parsed;
        ctx.hasConfig = true;
        ctx.configStore.saveConfig(parsed);

        char buf[512];
        const size_t respLen = serializeDeviceConfig(parsed, buf, sizeof(buf));
        request->send(200, "application/json", String(buf, respLen));
      });
}

}  // namespace ofd::app
