#include "domain/protocol.h"

#include <ArduinoJson.h>

#include <cstdio>
#include <cstring>

#include "domain/time_util.h"

namespace ofd {

namespace {
void setError(char* errorOut, size_t errorOutLen, const char* message) {
  if (errorOut == nullptr || errorOutLen == 0) return;
  std::snprintf(errorOut, errorOutLen, "%s", message);
}

void copyBounded(const char* src, char* dst, size_t dstLen) {
  std::strncpy(dst, src, dstLen - 1);
  dst[dstLen - 1] = '\0';
}

EmergencyState parseEmergencyState(const char* raw) {
  if (std::strcmp(raw, "general") == 0) return EmergencyState::General;
  if (std::strcmp(raw, "medical") == 0) return EmergencyState::Medical;
  if (std::strcmp(raw, "minimum-fuel") == 0) return EmergencyState::MinimumFuel;
  if (std::strcmp(raw, "no-communications") == 0) return EmergencyState::NoCommunications;
  if (std::strcmp(raw, "unlawful-interference") == 0) return EmergencyState::UnlawfulInterference;
  if (std::strcmp(raw, "downed") == 0) return EmergencyState::Downed;
  return EmergencyState::None;
}

ProviderHealth parseProviderHealth(const char* raw) {
  if (std::strcmp(raw, "degraded") == 0) return ProviderHealth::Degraded;
  if (std::strcmp(raw, "unavailable") == 0) return ProviderHealth::Unavailable;
  return ProviderHealth::Ok;
}
}  // namespace

// Sized for the worst case (a full 10-aircraft aircraft-update message).
// This is an engineering estimate, not a measured figure -- verify
// against ArduinoJson's capacity assistant once this is buildable
// (docs/CORE2_HARDWARE.md).
constexpr size_t kServerMessageJsonCapacity = 4096;

bool parseServerMessage(const char* json, size_t len, ParsedServerMessage& out, char* errorOut,
                         size_t errorOutLen) {
  if (json == nullptr || len == 0) {
    setError(errorOut, errorOutLen, "empty payload");
    return false;
  }

  StaticJsonDocument<kServerMessageJsonCapacity> doc;
  const DeserializationError err = deserializeJson(doc, json, len);
  if (err) {
    setError(errorOut, errorOutLen, "malformed JSON");
    return false;
  }

  const int schemaVersion = doc["schemaVersion"] | -1;
  if (schemaVersion != kCurrentSchemaVersion) {
    setError(errorOut, errorOutLen, "unsupported schemaVersion");
    return false;
  }

  const char* type = doc["type"] | "";

  if (std::strcmp(type, "heartbeat") == 0) {
    out.type = ServerMessageType::Heartbeat;
    return true;
  }

  if (std::strcmp(type, "provider-status") == 0) {
    out.type = ServerMessageType::ProviderStatus;
    copyBounded(doc["provider"] | "", out.providerId, sizeof(out.providerId));
    out.providerHealth = parseProviderHealth(doc["status"] | "ok");
    copyBounded(doc["message"] | "", out.statusMessage, sizeof(out.statusMessage));
    return true;
  }

  if (std::strcmp(type, "aircraft-update") == 0) {
    JsonArrayConst arr = doc["aircraft"];
    AircraftList list;
    for (JsonObjectConst item : arr) {
      if (list.count >= kMaxAircraftPerUpdate) break;  // enforce the bound even if the sender didn't
      AircraftState state;

      copyBounded(item["icaoHex"] | "", state.icaoHex, sizeof(state.icaoHex));
      if (item.containsKey("callsign")) {
        state.hasCallsign = true;
        copyBounded(item["callsign"] | "", state.callsign, sizeof(state.callsign));
      }
      state.latitude = item["latitude"] | 0.0;
      state.longitude = item["longitude"] | 0.0;

      if (item.containsKey("geometricAltitudeFt")) {
        state.hasAltitudeFt = true;
        state.altitudeFt = item["geometricAltitudeFt"] | 0.0;
      } else if (item.containsKey("barometricAltitudeFt")) {
        state.hasAltitudeFt = true;
        state.altitudeFt = item["barometricAltitudeFt"] | 0.0;
      }

      if (item.containsKey("groundSpeedKt")) {
        state.hasGroundSpeedKt = true;
        state.groundSpeedKt = item["groundSpeedKt"] | 0.0;
      }
      if (item.containsKey("verticalRateFtPerMin")) {
        state.hasVerticalRateFtPerMin = true;
        state.verticalRateFtPerMin = item["verticalRateFtPerMin"] | 0.0;
      }
      state.onGround = item["onGround"] | false;
      state.emergencyState = parseEmergencyState(item["emergencyState"] | "none");

      if (item.containsKey("distanceFromObserverKm")) {
        state.hasDistanceFromObserverKm = true;
        state.distanceFromObserverKm = item["distanceFromObserverKm"] | 0.0;
      }
      if (item.containsKey("bearingFromObserverDeg")) {
        state.hasBearingFromObserverDeg = true;
        state.bearingFromObserverDeg = item["bearingFromObserverDeg"] | 0.0;
      }

      int64_t epochMs = 0;
      const char* ts = item["positionTimestamp"] | "";
      if (parseIso8601ToEpochMs(ts, epochMs)) {
        state.positionTimestampMs = epochMs;
      }

      list.items[list.count++] = state;
    }
    out.type = ServerMessageType::AircraftUpdate;
    out.aircraft = list;
    return true;
  }

  setError(errorOut, errorOutLen, "unrecognized message type");
  return false;
}

size_t buildHelloMessage(const char* deviceId, char* buf, size_t bufLen) {
  StaticJsonDocument<192> doc;
  doc["schemaVersion"] = kCurrentSchemaVersion;
  doc["type"] = "hello";
  doc["deviceId"] = deviceId;
  doc["role"] = "core2";
  const size_t written = serializeJson(doc, buf, bufLen);
  return written < bufLen ? written : 0;
}

}  // namespace ofd
