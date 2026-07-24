#include "domain/config.h"

#include <ArduinoJson.h>

#include <cstdio>
#include <cstring>

namespace ofd {

namespace {
void setError(char* errorOut, size_t errorOutLen, const char* message) {
  if (errorOut == nullptr || errorOutLen == 0) return;
  std::snprintf(errorOut, errorOutLen, "%s", message);
}

bool copyBounded(const char* src, char* dst, size_t dstLen) {
  const size_t srcLen = std::strlen(src);
  if (srcLen == 0 || srcLen >= dstLen) return false;
  std::memcpy(dst, src, srcLen + 1);
  return true;
}
}  // namespace

// Bounded to 512 bytes -- a Phase 1 config payload (device name, gateway
// URL, one circular area) comfortably fits well under that; a larger
// payload is rejected outright rather than growing the buffer, per
// docs/CORE2_HARDWARE.md's "no unbounded allocation" rule.
constexpr size_t kConfigJsonCapacity = 512;

// File-scope (not function-local) statics -- see the detailed note in
// domain/protocol.cpp on why a *function-local* static of a non-POD
// ArduinoJson type is itself a stack-overflow risk (its lazy-init guard
// registers an atexit handler on first use, which was enough on its own
// to blow the stack in a deep call chain). These are constructed once at
// startup instead. Two separate instances (parse vs. serialize) since
// they're never needed at the same time but keeping them distinct avoids
// any risk of one function's leftover state bleeding into the other.
static StaticJsonDocument<kConfigJsonCapacity> g_configParseDoc;
static StaticJsonDocument<kConfigJsonCapacity> g_configSerializeDoc;

bool parseAndValidateDeviceConfig(const char* json, size_t len, DeviceConfig& out, char* errorOut,
                                   size_t errorOutLen) {
  if (json == nullptr || len == 0) {
    setError(errorOut, errorOutLen, "empty payload");
    return false;
  }

  StaticJsonDocument<kConfigJsonCapacity>& doc = g_configParseDoc;
  const DeserializationError err = deserializeJson(doc, json, len);
  if (err) {
    setError(errorOut, errorOutLen, "malformed JSON");
    return false;
  }

  DeviceConfig parsed;

  const char* deviceId = doc["deviceId"] | "";
  if (!copyBounded(deviceId, parsed.deviceId, sizeof(parsed.deviceId))) {
    setError(errorOut, errorOutLen, "deviceId missing or too long");
    return false;
  }

  const char* deviceName = doc["deviceName"] | "OpenFlightDisplay";
  if (!copyBounded(deviceName, parsed.deviceName, sizeof(parsed.deviceName))) {
    setError(errorOut, errorOutLen, "deviceName empty or too long");
    return false;
  }

  if (doc.containsKey("gatewayUrl")) {
    const char* url = doc["gatewayUrl"] | "";
    const bool looksLikeWsUrl = std::strncmp(url, "ws://", 5) == 0 || std::strncmp(url, "wss://", 6) == 0;
    if (!looksLikeWsUrl || !copyBounded(url, parsed.gatewayUrl, sizeof(parsed.gatewayUrl))) {
      setError(errorOut, errorOutLen, "gatewayUrl must be a ws:// or wss:// URL");
      return false;
    }
    parsed.hasGatewayUrl = true;
  }

  if (doc.containsKey("monitoringArea")) {
    JsonObjectConst area = doc["monitoringArea"];
    const char* kind = area["kind"] | "";
    if (std::strcmp(kind, "circle") != 0) {
      setError(errorOut, errorOutLen, "monitoringArea.kind not yet supported (circle only)");
      return false;
    }

    CircleMonitoringArea circle;
    circle.centerLat = area["centerLat"] | 0.0;
    circle.centerLon = area["centerLon"] | 0.0;
    circle.radiusKm = area["radiusKm"] | 0.0;
    if (circle.centerLat < -90.0 || circle.centerLat > 90.0) {
      setError(errorOut, errorOutLen, "centerLat out of range");
      return false;
    }
    if (circle.centerLon < -180.0 || circle.centerLon > 180.0) {
      setError(errorOut, errorOutLen, "centerLon out of range");
      return false;
    }
    if (circle.radiusKm < 0.5 || circle.radiusKm > 500.0) {
      setError(errorOut, errorOutLen, "radiusKm out of range [0.5, 500]");
      return false;
    }
    if (area.containsKey("minAltitudeFt")) {
      circle.hasMinAltitudeFt = true;
      circle.minAltitudeFt = area["minAltitudeFt"] | 0.0;
    }
    if (area.containsKey("maxAltitudeFt")) {
      circle.hasMaxAltitudeFt = true;
      circle.maxAltitudeFt = area["maxAltitudeFt"] | 0.0;
    }
    parsed.monitoringArea = circle;
    parsed.hasMonitoringArea = true;
  }

  if (doc.containsKey("displayProfile") && doc["displayProfile"].containsKey("brightness")) {
    const int brightness = doc["displayProfile"]["brightness"] | 200;
    if (brightness < 10 || brightness > 255) {
      setError(errorOut, errorOutLen, "brightness out of range [10, 255]");
      return false;
    }
    parsed.brightness = static_cast<uint8_t>(brightness);
  }

  out = parsed;
  return true;
}

size_t serializeDeviceConfig(const DeviceConfig& config, char* buf, size_t bufLen) {
  StaticJsonDocument<kConfigJsonCapacity>& doc = g_configSerializeDoc;
  doc.clear();
  doc["deviceId"] = config.deviceId;
  doc["deviceName"] = config.deviceName;
  if (config.hasGatewayUrl) doc["gatewayUrl"] = config.gatewayUrl;

  if (config.hasMonitoringArea) {
    JsonObject area = doc.createNestedObject("monitoringArea");
    area["kind"] = "circle";
    area["centerLat"] = config.monitoringArea.centerLat;
    area["centerLon"] = config.monitoringArea.centerLon;
    area["radiusKm"] = config.monitoringArea.radiusKm;
    if (config.monitoringArea.hasMinAltitudeFt) area["minAltitudeFt"] = config.monitoringArea.minAltitudeFt;
    if (config.monitoringArea.hasMaxAltitudeFt) area["maxAltitudeFt"] = config.monitoringArea.maxAltitudeFt;
  }

  JsonObject display = doc.createNestedObject("displayProfile");
  display["brightness"] = config.brightness;

  const size_t written = serializeJson(doc, buf, bufLen);
  return written < bufLen ? written : 0;
}

}  // namespace ofd
