#include "app/adsb_provider.h"

#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <WiFiClientSecure.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <time.h>

#include <cstring>

#include "domain/airline.h"
#include "domain/ranking.h"
#include "domain/staleness.h"

namespace ofd::app {

namespace {

constexpr uint32_t kPollIntervalMs = 15000;
constexpr uint32_t kHttpTimeoutMs = 8000;
constexpr uint32_t kTaskStackWords = 16384 / sizeof(StackType_t);
constexpr UBaseType_t kTaskPriority = 1;
constexpr double kKnotsToMph = 1.15078;

AppContext* g_ctx = nullptr;
constexpr size_t kResponseCapacity = 16384;
static StaticJsonDocument<kResponseCapacity> g_doc;

// ---- helpers ----

void copyBounded(const char* src, char* dst, size_t dstLen) {
  std::strncpy(dst, src, dstLen - 1);
  dst[dstLen - 1] = '\0';
}

// ---- adsb.lol field normalization ----

void parseAdsbAircraft(JsonObjectConst item, ofd::AircraftState& out) {
  // ICAO hex — required, skip if missing
  const char* hex = item["hex"] | "";
  if (std::strlen(hex) != 6) return;
  copyBounded(hex, out.icaoHex, sizeof(out.icaoHex));

  // Callsign
  if (item.containsKey("flight")) {
    const char* cs = item["flight"] | "";
    if (cs[0] != '\0') {
      out.hasCallsign = true;
      copyBounded(cs, out.callsign, sizeof(out.callsign));
    }
  }

  // Airline resolution from callsign
  if (out.hasCallsign) {
    extractAirlinePrefix(out.callsign, out.airlineIcao, sizeof(out.airlineIcao));
    const char* airline = resolveAirlineName(out.callsign);
    if (airline != nullptr) {
      out.hasAirlineName = true;
      copyBounded(airline, out.airlineName, sizeof(out.airlineName));
    }
  }

  // Aircraft type code (adsb.lol field `t`)
  if (item.containsKey("t")) {
    const char* type = item["t"] | "";
    if (type[0] != '\0') {
      out.hasAircraftType = true;
      copyBounded(type, out.aircraftTypeCode, sizeof(out.aircraftTypeCode));
    }
  }

  out.latitude = item["lat"] | 0.0;
  out.longitude = item["lon"] | 0.0;

  // Altitude: prefer geometric, fall back to barometric
  if (item.containsKey("alt_geom") && item["alt_geom"].is<double>()) {
    out.hasAltitudeFt = true;
    out.altitudeFt = item["alt_geom"] | 0.0;
  } else if (item.containsKey("alt_baro") && item["alt_baro"].is<double>()) {
    out.hasAltitudeFt = true;
    out.altitudeFt = item["alt_baro"] | 0.0;
  }

  // Ground speed (knots)
  if (item.containsKey("gs") && item["gs"].is<double>()) {
    out.hasGroundSpeedKt = true;
    out.groundSpeedKt = item["gs"] | 0.0;
    // Convert to MPH once here
    out.hasGroundSpeedMph = true;
    out.groundSpeedMph = out.groundSpeedKt * kKnotsToMph;
  }

  // Track/heading (adsb.lol field `track`)
  if (item.containsKey("track") && item["track"].is<double>()) {
    out.hasTrackHeadingDeg = true;
    out.trackHeadingDeg = item["track"] | 0.0;
  }

  // Vertical rate
  double vrate = 0.0;
  bool hasVrate = false;
  if (item.containsKey("baro_rate") && item["baro_rate"].is<double>()) {
    vrate = item["baro_rate"] | 0.0;
    hasVrate = true;
  } else if (item.containsKey("geom_rate") && item["geom_rate"].is<double>()) {
    vrate = item["geom_rate"] | 0.0;
    hasVrate = true;
  }
  if (hasVrate) {
    out.hasVerticalRateFtPerMin = true;
    out.verticalRateFtPerMin = vrate;
  }

  // Squawk
  if (item.containsKey("squawk")) {
    const char* sq = item["squawk"] | "";
    if (sq[0] != '\0') {
      out.hasSquawk = true;
      copyBounded(sq, out.squawk, sizeof(out.squawk));
    }
  }

  // On-ground: "alt_baro" can be the string "ground"
  if (item.containsKey("alt_baro") && item["alt_baro"].is<const char*>()) {
    out.onGround = (std::strcmp(item["alt_baro"] | "", "ground") == 0);
  }

  // Emergency squawk
  const char* emergency = item["emergency"] | "";
  if (std::strcmp(emergency, "general") == 0) out.emergencyState = ofd::EmergencyState::General;
  else if (std::strcmp(emergency, "lifeguard") == 0) out.emergencyState = ofd::EmergencyState::Medical;
  else if (std::strcmp(emergency, "minfuel") == 0) out.emergencyState = ofd::EmergencyState::MinimumFuel;
  else if (std::strcmp(emergency, "nordo") == 0) out.emergencyState = ofd::EmergencyState::NoCommunications;
  else if (std::strcmp(emergency, "unlawful") == 0) out.emergencyState = ofd::EmergencyState::UnlawfulInterference;
  else if (std::strcmp(emergency, "downed") == 0) out.emergencyState = ofd::EmergencyState::Downed;

  // Use NTP/Unix time so staleness guard works correctly
  out.positionTimestampMs = static_cast<int64_t>(time(nullptr)) * 1000;
}

void pollTask(void*) {
  WiFiClientSecure client;
  client.setInsecure();
  HTTPClient http;

  for (;;) {
    if (g_ctx == nullptr || !g_ctx->hasConfig || !g_ctx->config.hasMonitoringArea) {
      vTaskDelay(pdMS_TO_TICKS(kPollIntervalMs));
      continue;
    }

    const auto& area = g_ctx->config.monitoringArea;
    const double radiusNm = area.radiusKm / 1.852;
    char url[192];
    std::snprintf(url, sizeof(url),
                  "https://api.adsb.lol/v2/point/%.4f/%.4f/%.1f",
                  area.centerLat, area.centerLon, radiusNm);

    http.begin(client, url);
    http.setTimeout(kHttpTimeoutMs);

    const int httpCode = http.GET();

    if (httpCode == 200) {
      const String payload = http.getString();
      g_doc.clear();
      const DeserializationError err = deserializeJson(g_doc, payload);

      if (!err) {
        JsonArrayConst ac = g_doc["ac"].as<JsonArrayConst>();

        ofd::AircraftList list;
        list.count = 0;
        for (JsonObjectConst item : ac) {
          if (list.count >= ofd::kMaxAircraftPerUpdate) break;
          ofd::AircraftState state;
          parseAdsbAircraft(item, state);
          if (state.icaoHex[0] != '\0' && state.latitude != 0.0 && state.longitude != 0.0) {
            list.items[list.count++] = state;
          }
        }

        const ofd::AircraftList ranked = ofd::rankNearest(list, area);

        g_ctx->latestAircraft = ranked;
        g_ctx->hasLatestAircraft = true;
        g_ctx->lastAircraftUpdateAtMs = millis();
        g_ctx->providerHealth = ofd::ProviderHealth::Ok;
      } else {
        g_ctx->providerHealth = ofd::ProviderHealth::Degraded;
      }
    } else {
      g_ctx->providerHealth = (httpCode < 0)
                                 ? ofd::ProviderHealth::Unavailable
                                 : ofd::ProviderHealth::Degraded;
    }

    http.end();
    vTaskDelay(pdMS_TO_TICKS(kPollIntervalMs));
  }
}

}  // namespace

void AdsbProvider::begin(AppContext& ctx) {
  g_ctx = &ctx;
  if (!m_started) {
    m_started = true;
    xTaskCreate(pollTask, "adsbPoll", kTaskStackWords, nullptr, kTaskPriority, nullptr);
  }
}

}  // namespace ofd::app