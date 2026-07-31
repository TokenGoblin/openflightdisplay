#include "domain/config.h"

#include <ArduinoJson.h>

#include <cstdio>
#include <cstring>

#include "domain/flight_tracking.h"

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
// An ICAO airport identifier: exactly four letters (KSEA, EGLL, YSSY).
// IATA codes are deliberately rejected rather than guessed at -- the
// airport endpoint this feeds (/api/0/airport/{icao}) answers `null` for
// IATA, and there is no safe way to expand "SEA" into "KSEA" without a
// lookup table (the K prefix is North America only). The setup UI is
// responsible for handing us ICAO; failing here with a clear reason beats
// silently tracking a flight to nowhere.
bool isIcaoAirportCode(const char* code) {
  if (code == nullptr || std::strlen(code) != 4) return false;
  for (int i = 0; i < 4; i++) {
    const char c = code[i];
    const bool alpha = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    if (!alpha) return false;
  }
  return true;
}

void upperCopy(const char* src, char* dst, size_t dstLen) {
  size_t i = 0;
  for (; src[i] != '\0' && i < dstLen - 1; i++) {
    const char c = src[i];
    dst[i] = (c >= 'a' && c <= 'z') ? static_cast<char>(c - 'a' + 'A') : c;
  }
  dst[i] = '\0';
}

}  // namespace

// Raised from 512 when the optional trackedFlight object was added --
// a full config with a monitoring area, a display profile and a tracked
// flight no longer fits the old capacity, and ArduinoJson fails a parse
// by silently truncating rather than erroring.
constexpr size_t kConfigJsonCapacity = 768;

// File-scope statics to avoid atexit/lazy-init stack overflow
// (see detailed note in domain/protocol.cpp).
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

  // deviceId is optional in the payload -- if missing, fall back to
  // whatever `out` already held (a partial PUT that only touches, say,
  // brightness shouldn't have to resend it). What's not optional is the
  // *result*: a config with no deviceId at all (neither supplied nor
  // pre-existing) is rejected below, rather than silently persisted.
  if (doc.containsKey("deviceId")) {
    const char* deviceId = doc["deviceId"] | "";
    if (!copyBounded(deviceId, parsed.deviceId, sizeof(parsed.deviceId))) {
      setError(errorOut, errorOutLen, "deviceId missing or too long");
      return false;
    }
  } else if (out.deviceId[0] != '\0') {
    std::strncpy(parsed.deviceId, out.deviceId, sizeof(parsed.deviceId) - 1);
  }

  if (doc.containsKey("deviceName")) {
    const char* deviceName = doc["deviceName"] | "";
    if (!copyBounded(deviceName, parsed.deviceName, sizeof(parsed.deviceName))) {
      setError(errorOut, errorOutLen, "deviceName empty or too long");
      return false;
    }
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

  // Tracked flight. Three-way rather than two: absent means "leave
  // whatever is already set alone" (so a PUT changing only brightness
  // doesn't cancel someone's airport run), explicit null means "stop
  // tracking", and an object means "track this".
  if (doc.containsKey("trackedFlight")) {
    if (doc["trackedFlight"].isNull()) {
      parsed.hasTrackedFlight = false;
    } else {
      JsonObjectConst tf = doc["trackedFlight"];
      const char* flight = tf["flight"] | "";
      const char* destination = tf["destinationIcao"] | "";

      TrackedFlightConfig tracked;
      if (!normalizeFlightIdentifier(flight, tracked.callsign, sizeof(tracked.callsign))) {
        setError(errorOut, errorOutLen, "trackedFlight.flight is not a flight number or callsign");
        return false;
      }
      if (!isIcaoAirportCode(destination)) {
        setError(errorOut, errorOutLen, "trackedFlight.destinationIcao must be a 4-letter ICAO code");
        return false;
      }
      if (!copyBounded(flight, tracked.label, sizeof(tracked.label))) {
        setError(errorOut, errorOutLen, "trackedFlight.flight too long");
        return false;
      }
      upperCopy(destination, tracked.destinationIcao, sizeof(tracked.destinationIcao));

      // Bounded rather than trusted: these feed a subtraction whose
      // result is rendered as "LEAVE NOW", and an absurd travel time
      // would either suppress the advice forever or fire it immediately.
      if (tf.containsKey("travelMinutes")) {
        const int travel = tf["travelMinutes"] | 0;
        if (travel < 0 || travel > 720) {
          setError(errorOut, errorOutLen, "trackedFlight.travelMinutes out of range [0, 720]");
          return false;
        }
        tracked.travelMinutes = static_cast<uint16_t>(travel);
      }
      if (tf.containsKey("postLandingMinutes")) {
        const int postLanding = tf["postLandingMinutes"] | 0;
        if (postLanding < 0 || postLanding > 240) {
          setError(errorOut, errorOutLen, "trackedFlight.postLandingMinutes out of range [0, 240]");
          return false;
        }
        tracked.postLandingMinutes = static_cast<uint16_t>(postLanding);
      }

      parsed.trackedFlight = tracked;
      parsed.hasTrackedFlight = true;
    }
  } else {
    parsed.hasTrackedFlight = out.hasTrackedFlight;
    parsed.trackedFlight = out.trackedFlight;
  }

  if (doc.containsKey("displayProfile") && doc["displayProfile"].containsKey("brightness")) {
    const int brightness = doc["displayProfile"]["brightness"] | 200;
    if (brightness < 10 || brightness > 255) {
      setError(errorOut, errorOutLen, "brightness out of range [10, 255]");
      return false;
    }
    parsed.brightness = static_cast<uint8_t>(brightness);
  }

  if (parsed.deviceId[0] == '\0') {
    setError(errorOut, errorOutLen, "deviceId missing or too long");
    return false;
  }

  out = parsed;
  return true;
}

size_t serializeDeviceConfig(const DeviceConfig& config, char* buf, size_t bufLen) {
  StaticJsonDocument<kConfigJsonCapacity>& doc = g_configSerializeDoc;
  doc.clear();
  doc["deviceId"] = config.deviceId;
  doc["deviceName"] = config.deviceName;

  if (config.hasMonitoringArea) {
    JsonObject area = doc.createNestedObject("monitoringArea");
    area["kind"] = "circle";
    area["centerLat"] = config.monitoringArea.centerLat;
    area["centerLon"] = config.monitoringArea.centerLon;
    area["radiusKm"] = config.monitoringArea.radiusKm;
    if (config.monitoringArea.hasMinAltitudeFt) area["minAltitudeFt"] = config.monitoringArea.minAltitudeFt;
    if (config.monitoringArea.hasMaxAltitudeFt) area["maxAltitudeFt"] = config.monitoringArea.maxAltitudeFt;
  }

  if (config.hasTrackedFlight) {
    JsonObject tracked = doc.createNestedObject("trackedFlight");
    tracked["flight"] = config.trackedFlight.label;
    tracked["callsign"] = config.trackedFlight.callsign;
    tracked["destinationIcao"] = config.trackedFlight.destinationIcao;
    tracked["travelMinutes"] = config.trackedFlight.travelMinutes;
    tracked["postLandingMinutes"] = config.trackedFlight.postLandingMinutes;
  }

  JsonObject display = doc.createNestedObject("displayProfile");
  display["brightness"] = config.brightness;

  const size_t written = serializeJson(doc, buf, bufLen);
  return written < bufLen ? written : 0;
}

}  // namespace ofd