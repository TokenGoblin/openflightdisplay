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

// File-scope (not function-local) statics for every ArduinoJson document
// used in this file -- see domain/protocol.cpp's detailed note on why a
// *function-local* static of a non-POD type is itself a stack-overflow
// risk here (its lazy-init guard registers an atexit handler on first
// use, confirmed via a symbolicated crash to be enough on its own to
// blow the stack in a deep call chain). File-scope statics are
// constructed once at startup instead, with no per-call guard check.
// Safe to share across these handlers: ESPAsyncWebServer's async task
// processes one callback at a time, not concurrently.
StaticJsonDocument<128> g_errorDoc;
StaticJsonDocument<192> g_pairRequestDoc;
StaticJsonDocument<192> g_pairResponseDoc;
StaticJsonDocument<256> g_statusDoc;
StaticJsonDocument<kBodyBufferCapacity> g_configWrapperDoc;

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
  g_errorDoc.clear();
  g_errorDoc["schemaVersion"] = 1;
  g_errorDoc["error"] = error;
  char buf[128];
  const size_t len = serializeJson(g_errorDoc, buf, sizeof(buf));
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
  // CORS: the PWA is served from its own origin (a dev server or static
  // host), never the Core2's own IP, so every response here is
  // cross-origin from the browser's perspective. Verified needed on real
  // hardware -- without this, the PWA's fetch() calls failed with a
  // generic "Load failed"/"Failed to fetch" because the browser's CORS
  // preflight (OPTIONS) had nothing to talk to and blocked the real
  // request before it ever reached the device.
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Origin", "*");
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

  server.on("/pair", HTTP_OPTIONS, [](AsyncWebServerRequest* request) { request->send(200); });
  server.on("/api/v1/config", HTTP_OPTIONS, [](AsyncWebServerRequest* request) { request->send(200); });

  // NOTE (unverified without hardware): whether ESPAsyncWebServer invokes
  // the onBody callback at all for a genuinely empty (Content-Length: 0)
  // POST varies by version. A deliberate choice was made NOT to also
  // handle that case in the onRequest callback below, since doing so
  // risks a double request->send() (undefined behavior in this framework)
  // if onBody *does* still fire with total==0. Worst case today: a
  // malformed empty-body request hangs until the client's own timeout,
  // rather than crashing. Revisit once this is buildable against the
  // real library.
  // Verified on real hardware: a phone's camera app treats the QR code's
  // "http://<ip>/pair?code=..." payload as a link and opens it directly
  // in the browser (a plain GET), rather than handing it to the
  // OpenFlightDisplay PWA's own in-app scanner as originally intended.
  // Since /pair was previously POST-only, that GET hit nothing and
  // rendered as a dead page. This handler exists purely to give that
  // browser navigation somewhere useful to land -- it deliberately does
  // NOT claim the pairing code (tryClaim is single-use), so the PWA's
  // own pairing flow (camera scan within the app, or manual entry) still
  // works afterward exactly as before.
  server.on("/pair", HTTP_GET, [](AsyncWebServerRequest* request) {
    request->send(200, "text/html",
                   "<!DOCTYPE html><html><head><meta name=\"viewport\" "
                   "content=\"width=device-width, initial-scale=1\">"
                   "<title>OpenFlightDisplay pairing</title></head>"
                   "<body style=\"font-family: sans-serif; max-width: 420px; margin: 2rem auto; padding: 0 1rem;\">"
                   "<h2>Open the OpenFlightDisplay app to finish pairing</h2>"
                   "<p>This code is meant to be scanned from inside the OpenFlightDisplay "
                   "tablet app's \"Add Display\" screen (or entered there manually), not opened "
                   "as a regular link.</p>"
                   "</body></html>");
  });

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

        if (deserializeJson(g_pairRequestDoc, g_bodyBuffer, total) || (g_pairRequestDoc["schemaVersion"] | -1) != 1) {
          sendJsonError(request, 400, "invalid_request");
          return;
        }
        const char* code = g_pairRequestDoc["code"] | "";
        if (!ctx.pairingCodeManager.tryClaim(code, millis())) {
          sendJsonError(request, 401, "invalid_or_expired_code");
          return;
        }

        char token[40];
        generatePairingToken(token, sizeof(token));
        std::strcpy(ctx.pairingToken, token);
        ctx.hasPairingToken = true;
        ctx.configStore.savePairingToken(token);

        g_pairResponseDoc.clear();
        g_pairResponseDoc["schemaVersion"] = 1;
        g_pairResponseDoc["pairingToken"] = token;
        g_pairResponseDoc["deviceId"] = ctx.deviceId;
        char buf[192];
        const size_t respLen = serializeJson(g_pairResponseDoc, buf, sizeof(buf));
        request->send(200, "application/json", String(buf, respLen));
      });

  server.on("/api/v1/status", HTTP_GET, [&ctx](AsyncWebServerRequest* request) {
    g_statusDoc.clear();
    g_statusDoc["schemaVersion"] = 1;
    g_statusDoc["deviceId"] = ctx.deviceId;
    g_statusDoc["firmwareVersion"] = ctx.firmwareVersion;
    g_statusDoc["wifiState"] = wifiStateToString(ctx.wifiState);
    g_statusDoc["gatewayConnectionState"] = gatewayStateToString(ctx.gatewayState);
    if (ctx.hasLatestAircraft) {
      g_statusDoc["lastAircraftUpdateAgeSeconds"] = (millis() - ctx.lastAircraftUpdateAtMs) / 1000;
    }
    g_statusDoc["freeHeapBytes"] = ESP.getFreeHeap();
    char buf[256];
    const size_t len = serializeJson(g_statusDoc, buf, sizeof(buf));
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
        // g_configWrapperDoc is file-scope (see the note near its
        // declaration) -- at kBodyBufferCapacity (1024) bytes, this was
        // the single biggest stack consumer in this file.
        if (deserializeJson(g_configWrapperDoc, g_bodyBuffer, total) ||
            (g_configWrapperDoc["schemaVersion"] | -1) != 1 || !g_configWrapperDoc.containsKey("config")) {
          sendJsonError(request, 400, "invalid_config");
          return;
        }
        // A plain char array has no constructor, so it never needed the
        // file-scope treatment above to avoid the atexit/lazy-init issue
        // -- `static` alone (moving it off the stack) was always fine
        // for this one.
        static char configJson[kBodyBufferCapacity];
        const size_t configLen = serializeJson(g_configWrapperDoc["config"], configJson, sizeof(configJson));

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
