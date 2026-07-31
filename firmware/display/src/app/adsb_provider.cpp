#include "app/adsb_provider.h"

#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <WiFiClientSecure.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include <time.h>

#include <cstring>

#include "domain/airline.h"
#include "domain/flight_tracking.h"
#include "domain/ranking.h"
#include "domain/staleness.h"

namespace ofd::app {

namespace {

constexpr uint32_t kPollIntervalMs = 15000;
constexpr uint32_t kHttpTimeoutMs = 8000;
constexpr uint32_t kTaskStackWords = 16384 / sizeof(StackType_t);
constexpr UBaseType_t kTaskPriority = 1;
constexpr double kKnotsToMph = 1.15078;
// How often the task wakes to re-check its two independent deadlines.
// Fine-grained enough that the tracked flight's fastest cadence
// (kMinPollIntervalMs, 10s) isn't measurably delayed, coarse enough to
// cost nothing.
constexpr uint32_t kTaskTickMs = 1000;
// Widest geographic query actually issued, regardless of configured
// radius. Sized from measured responses (28 aircraft / 13.7KB at 50 NM,
// 75 / 43KB at 150 NM) against the fixed parse buffer below, with the
// field filter applied. Even this can overflow over exceptionally busy
// airspace -- the difference is that overflow is now reported on serial
// instead of silently blanking the screen forever.
constexpr double kMaxQueryRadiusNm = 80.0;

AppContext* g_ctx = nullptr;
constexpr size_t kResponseCapacity = 16384;
static StaticJsonDocument<kResponseCapacity> g_doc;

// ---- response filter ----
//
// adsb.lol returns ~51 fields per aircraft; this firmware reads 13. The
// rest (signal strength, message counts, navigation-accuracy categories,
// wind and temperature aloft, ...) are parsed into memory and
// immediately discarded.
//
// Filtering them out at parse time is measured at a **69.5% reduction**
// against a real worst-case response (114 aircraft: 66,082 bytes raw ->
// 20,149 filtered), which roughly triples how much airspace fits in the
// fixed buffer below. Built once at startup rather than per poll: it is
// constant, and rebuilding it 5,760 times a day would be pure waste.
static StaticJsonDocument<256> g_filter;
bool g_filterReady = false;

void ensureFilter() {
  if (g_filterReady) return;
  JsonObject aircraft = g_filter.createNestedArray("ac").createNestedObject();
  for (const char* field : {"hex", "flight", "t", "lat", "lon", "alt_geom", "alt_baro", "gs", "track",
                            "baro_rate", "geom_rate", "squawk", "emergency"}) {
    aircraft[field] = true;
  }
  g_filterReady = true;
}

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

  // Callsign. ADS-B pads these to 8 characters ("BAW249  "), which is
  // invisible in a left-aligned label but breaks any comparison -- and
  // flight tracking compares this against a configured callsign on
  // every poll.
  if (item.containsKey("flight")) {
    const char* cs = item["flight"] | "";
    char trimmed[sizeof(out.callsign)];
    ofd::trimCallsign(cs, trimmed, sizeof(trimmed));
    if (trimmed[0] != '\0') {
      out.hasCallsign = true;
      copyBounded(trimmed, out.callsign, sizeof(out.callsign));
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

// One GET into g_doc. Returns the HTTP status; the caller decides what a
// non-200 means for its own health reporting, since a failed
// nearest-aircraft poll and a failed airport lookup are not the same
// kind of problem.
// `filtered` applies the aircraft field filter above; the small
// endpoints (airport lookup) pass false and take the whole object.
//
// Parses straight from the socket rather than via http.getString().
// That call materialises the entire response as a heap String before
// parsing begins -- up to 77KB on a wide-radius poll, held at the same
// time as the parse buffer *and* an open TLS session. Streaming reads it
// in chunks instead, so peak heap no longer scales with how busy the sky
// is.
int getJson(WiFiClientSecure& client, HTTPClient& http, const char* url, bool filtered,
            bool& parsedOut) {
  parsedOut = false;
  http.begin(client, url);
  http.setTimeout(kHttpTimeoutMs);
  const int httpCode = http.GET();
  if (httpCode == 200) {
    g_doc.clear();
    DeserializationError err;
    if (filtered) {
      ensureFilter();
      err = deserializeJson(g_doc, http.getStream(), DeserializationOption::Filter(g_filter));
    } else {
      err = deserializeJson(g_doc, http.getStream());
    }
    parsedOut = !err;
    if (err) {
      // Never fail silently here. A NoMemory in particular means the
      // response outgrew the buffer -- almost always because the
      // configured radius covers more aircraft than will fit -- and the
      // visible symptom is a display that just never shows anything.
      // Somebody debugging that deserves to be told where to look.
      Serial.printf("[adsb] parse failed (%s) for %s\n", err.c_str(), url);
      if (err == DeserializationError::NoMemory) {
        Serial.printf("[adsb] response exceeded the %u-byte buffer -- reduce the monitoring radius\n",
                      static_cast<unsigned>(kResponseCapacity));
      }
    }
  }
  http.end();
  return httpCode;
}

// ---- job 1: nearest aircraft in the monitoring area ----

void pollNearest(WiFiClientSecure& client, HTTPClient& http) {
  const auto& area = g_ctx->config.monitoringArea;

  // The configured radius is clamped for the *query*, not rejected in
  // config validation -- tightening the validator would fail an already
  // saved config on load and drop a working device into
  // "configuration required" after an update.
  //
  // Clamping is also simply more correct here. This screen shows the
  // nearest aircraft (and at most kMaxAircraftPerUpdate of them): the
  // nearest aircraft is the nearest whether the query covered 80 NM or
  // 270. A wider radius adds only payload -- measured at 13,685 bytes
  // for 50 NM against 77,286 for 270 NM, the latter far past what the
  // parse buffer can hold, which used to leave the display permanently
  // and silently blank.
  double radiusNm = area.radiusKm / 1.852;
  if (radiusNm > kMaxQueryRadiusNm) {
    static bool warned = false;
    if (!warned) {
      warned = true;
      Serial.printf("[adsb] querying %.0f NM instead of the configured %.0f NM -- beyond this the "
                    "response outgrows the parse buffer, and the nearest aircraft is unaffected\n",
                    kMaxQueryRadiusNm, radiusNm);
    }
    radiusNm = kMaxQueryRadiusNm;
  }

  char url[192];
  std::snprintf(url, sizeof(url), "https://api.adsb.lol/v2/point/%.4f/%.4f/%.1f", area.centerLat,
                area.centerLon, radiusNm);

  bool parsed = false;
  const int httpCode = getJson(client, http, url, /*filtered=*/true, parsed);

  if (httpCode != 200) {
    g_ctx->providerHealth =
        (httpCode < 0) ? ofd::ProviderHealth::Unavailable : ofd::ProviderHealth::Degraded;
    return;
  }
  if (!parsed) {
    g_ctx->providerHealth = ofd::ProviderHealth::Degraded;
    return;
  }

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

  g_ctx->latestAircraft = ofd::rankNearest(list, area);
  g_ctx->hasLatestAircraft = true;
  g_ctx->lastAircraftUpdateAtMs = millis();
  g_ctx->providerHealth = ofd::ProviderHealth::Ok;
}

// ---- job 2: the tracked flight ----

// Resolves the configured destination ICAO to coordinates and field
// elevation, once per tracked flight. Field elevation is why this lookup
// is worth a request at all: "on the ground" is judged against the
// destination's own elevation, and Denver's ramp is at 5,400 ft.
void resolveDestination(WiFiClientSecure& client, HTTPClient& http, const char* icao) {
  g_ctx->trackedDestination = ofd::Airport{};
  g_ctx->trackedDestinationUnresolved = false;

  char url[96];
  std::snprintf(url, sizeof(url), "https://api.adsb.lol/api/0/airport/%s", icao);

  bool parsed = false;
  const int httpCode = getJson(client, http, url, /*filtered=*/false, parsed);

  // The endpoint answers 200 with a literal `null` body for a code it
  // doesn't know (including any IATA code), so "parsed successfully" is
  // not the same as "found".
  if (httpCode != 200 || !parsed || g_doc.isNull() || !g_doc.containsKey("lat")) {
    g_ctx->trackedDestinationUnresolved = true;
    Serial.printf("[track] destination %s did not resolve (http %d)\n", icao, httpCode);
    return;
  }

  ofd::Airport airport;
  airport.valid = true;
  copyBounded(g_doc["icao"] | icao, airport.icao, sizeof(airport.icao));
  copyBounded(g_doc["iata"] | "", airport.iata, sizeof(airport.iata));
  copyBounded(g_doc["name"] | "", airport.name, sizeof(airport.name));
  airport.latitude = g_doc["lat"] | 0.0;
  airport.longitude = g_doc["lon"] | 0.0;
  airport.elevationFt = g_doc["alt_feet"] | 0.0;

  g_ctx->trackedDestination = airport;
  Serial.printf("[track] destination %s resolved: %s (%.4f, %.4f, %.0f ft)\n", icao, airport.name,
                airport.latitude, airport.longitude, airport.elevationFt);
}

// A direct callsign lookup -- one aircraft, not a geographic sweep. This
// is the whole efficiency argument: the nearest-aircraft query above
// returns every aircraft in the radius (dozens near a busy airport) and
// throws away all but the closest, while this returns exactly the one
// being followed.
void pollTrackedFlight(WiFiClientSecure& client, HTTPClient& http) {
  const auto& tracked = g_ctx->config.trackedFlight;

  char url[128];
  std::snprintf(url, sizeof(url), "https://api.adsb.lol/v2/callsign/%s", tracked.callsign);

  bool parsed = false;
  const int httpCode = getJson(client, http, url, /*filtered=*/true, parsed);
  if (httpCode != 200 || !parsed) return;  // keep the last known state; staleness handles it

  JsonArrayConst ac = g_doc["ac"].as<JsonArrayConst>();
  for (JsonObjectConst item : ac) {
    ofd::AircraftState state;
    parseAdsbAircraft(item, state);
    if (state.icaoHex[0] == '\0') continue;
    // The endpoint matches on the callsign, but confirm it rather than
    // trusting the first row: a mismatch here would silently show
    // somebody the wrong aircraft's position, and this is a screen
    // people make travel decisions from.
    if (std::strcmp(state.callsign, tracked.callsign) != 0) continue;

    g_ctx->trackedAircraft = state;
    g_ctx->trackedEverSeen = true;
    g_ctx->trackedLastSeenAtMs = millis();
    return;
  }
}

// ---- the shared task ----

void pollTask(void*) {
  WiFiClientSecure client;
  client.setInsecure();
  HTTPClient http;

  // Deadlines rather than sleeps, so the two jobs run on genuinely
  // independent cadences from one task. Compared as signed differences
  // so millis() wrapping at ~49 days doesn't stall either of them.
  uint32_t nextNearestAtMs = millis();
  uint32_t nextTrackedAtMs = millis();
  // Which flight the currently-resolved destination belongs to; a change
  // here means the user picked a different flight and every piece of
  // tracked state is stale.
  char resolvedFor[sizeof(ofd::TrackedFlightConfig::callsign)] = {0};

  for (;;) {
    if (g_ctx == nullptr || !g_ctx->hasConfig) {
      vTaskDelay(pdMS_TO_TICKS(kTaskTickMs));
      continue;
    }

    const uint32_t now = millis();

    if (g_ctx->config.hasMonitoringArea && static_cast<int32_t>(now - nextNearestAtMs) >= 0) {
      pollNearest(client, http);
      nextNearestAtMs = millis() + kPollIntervalMs;
    }

    if (g_ctx->config.hasTrackedFlight) {
      const auto& tracked = g_ctx->config.trackedFlight;

      if (std::strcmp(resolvedFor, tracked.callsign) != 0) {
        g_ctx->trackedAircraft = ofd::AircraftState{};
        g_ctx->trackedEverSeen = false;
        g_ctx->trackedLastSeenAtMs = 0;
        g_ctx->trackedProgress = ofd::FlightProgress{};
        g_ctx->trackedDeparture = ofd::DeparturePlan{};
        resolveDestination(client, http, tracked.destinationIcao);
        copyBounded(tracked.callsign, resolvedFor, sizeof(resolvedFor));
        nextTrackedAtMs = millis();
      }

      if (static_cast<int32_t>(millis() - nextTrackedAtMs) >= 0) {
        pollTrackedFlight(client, http);
        nextTrackedAtMs = millis() + ofd::pollIntervalMsFor(g_ctx->trackedProgress);
      }

      // Recomputed every tick, not just after a poll, so the countdown
      // and the silence timer stay honest between lookups -- and so
      // LostContact fires on schedule rather than at the next successful
      // poll, which by definition may never come.
      const uint32_t sinceSeen =
          g_ctx->trackedEverSeen ? (millis() - g_ctx->trackedLastSeenAtMs) / 1000 : 0;
      g_ctx->trackedProgress =
          ofd::computeFlightProgress(g_ctx->trackedAircraft, g_ctx->trackedDestination,
                                     g_ctx->trackedEverSeen, sinceSeen);
      g_ctx->trackedDeparture = ofd::computeDeparturePlan(
          g_ctx->trackedProgress, tracked.travelMinutes, tracked.postLandingMinutes);
    } else if (resolvedFor[0] != '\0') {
      resolvedFor[0] = '\0';
      g_ctx->trackedProgress = ofd::FlightProgress{};
      g_ctx->trackedDeparture = ofd::DeparturePlan{};
      g_ctx->trackedEverSeen = false;
      g_ctx->trackedDestination = ofd::Airport{};
      g_ctx->trackedDestinationUnresolved = false;
    }

    vTaskDelay(pdMS_TO_TICKS(kTaskTickMs));
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