#include "domain/display_format.h"

#include <cmath>
#include <cstdio>
#include <cstring>

#include "domain/staleness.h"

namespace ofd {

namespace {

constexpr double kKmToNm = 0.539957;
constexpr double kVerticalRateDeadbandFtPerMin = 300.0;

// Formats an integer with thousands separators, e.g. -12450 -> "-12,450".
// Pure fixed-buffer arithmetic -- no locale, no heap.
void formatIntWithCommas(long value, char* out, size_t outLen) {
  char digits[16];
  const bool negative = value < 0;
  unsigned long mag = negative ? static_cast<unsigned long>(-value) : static_cast<unsigned long>(value);

  int n = 0;
  do {
    digits[n++] = static_cast<char>('0' + (mag % 10));
    mag /= 10;
  } while (mag > 0 && n < static_cast<int>(sizeof(digits)));

  char grouped[24];
  int gi = 0;
  for (int i = n - 1; i >= 0; --i) {
    grouped[gi++] = digits[i];
    const int remaining = i;  // digits left to emit after this one
    if (remaining > 0 && remaining % 3 == 0) grouped[gi++] = ',';
  }
  grouped[gi] = '\0';

  if (negative) {
    std::snprintf(out, outLen, "-%s", grouped);
  } else {
    std::snprintf(out, outLen, "%s", grouped);
  }
}

void trimTrailingSpaces(char* s) {
  size_t len = std::strlen(s);
  while (len > 0 && (s[len - 1] == ' ' || s[len - 1] == '\t')) {
    s[--len] = '\0';
  }
}

void toUpper(char* s) {
  for (char* p = s; *p; ++p) {
    if (*p >= 'a' && *p <= 'z') *p = static_cast<char>(*p - 'a' + 'A');
  }
}

const char* compassFromDegrees(double deg) {
  if (deg < 0.0 || deg >= 360.0) return "\xE2\x80\x94";  // em dash
  static const char* kDirs[] = {"N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
                                 "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"};
  const int index = static_cast<int>((deg + 11.25) / 22.5) % 16;
  return kDirs[index];
}

}  // namespace

const char* motionStatusWord(MotionStatus status) {
  switch (status) {
    case MotionStatus::Ground: return "GROUND";
    case MotionStatus::Climb: return "CLIMB";
    case MotionStatus::Descent: return "DESCENT";
    case MotionStatus::Level: return "LEVEL";
    case MotionStatus::Airborne: return "AIRBORNE";
  }
  return "AIRBORNE";
}

const char* displayStatusWord(DisplayStatus status) {
  switch (status) {
    case DisplayStatus::Ground: return "GROUND";
    case DisplayStatus::Climb: return "CLIMB";
    case DisplayStatus::Descent: return "DESCENT";
    case DisplayStatus::Level: return "LEVEL";
    case DisplayStatus::Airborne: return "AIRBORNE";
    case DisplayStatus::Stale: return "STALE";
    case DisplayStatus::Emergency: return "EMERGENCY";
  }
  return "AIRBORNE";
}

StatusColorRole displayStatusColorRole(DisplayStatus status) {
  switch (status) {
    case DisplayStatus::Ground: return StatusColorRole::Neutral;
    case DisplayStatus::Climb:
    case DisplayStatus::Descent:
    case DisplayStatus::Level:
    case DisplayStatus::Airborne: return StatusColorRole::Good;
    case DisplayStatus::Stale: return StatusColorRole::Caution;
    case DisplayStatus::Emergency: return StatusColorRole::Critical;
  }
  return StatusColorRole::Neutral;
}

MotionStatus classifyMotionStatus(bool onGround, bool hasVerticalRateFtPerMin, double verticalRateFtPerMin) {
  if (onGround) return MotionStatus::Ground;
  if (!hasVerticalRateFtPerMin) return MotionStatus::Airborne;
  if (verticalRateFtPerMin >= kVerticalRateDeadbandFtPerMin) return MotionStatus::Climb;
  if (verticalRateFtPerMin <= -kVerticalRateDeadbandFtPerMin) return MotionStatus::Descent;
  return MotionStatus::Level;
}

void buildAircraftViewModel(const AircraftState& ac, uint32_t ageSeconds, bool stale, AircraftViewModel& out) {
  out = AircraftViewModel{};
  out.ageSeconds = ageSeconds;
  out.stale = stale;

  // ---- callsign ----
  if (ac.hasCallsign) {
    char buf[sizeof(ac.callsign)];
    std::strncpy(buf, ac.callsign, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';
    trimTrailingSpaces(buf);
    toUpper(buf);
    if (buf[0] != '\0') {
      std::strncpy(out.callsign, buf, sizeof(out.callsign) - 1);
      out.callsignIsPlaceholder = false;
    }
  }
  if (out.callsign[0] == '\0') {
    if (ac.icaoHex[0] != '\0') {
      char icaoUpper[sizeof(ac.icaoHex)];
      std::strncpy(icaoUpper, ac.icaoHex, sizeof(icaoUpper) - 1);
      icaoUpper[sizeof(icaoUpper) - 1] = '\0';
      toUpper(icaoUpper);
      std::strncpy(out.callsign, icaoUpper, sizeof(out.callsign) - 1);
    } else {
      std::strncpy(out.callsign, "NO CALLSIGN", sizeof(out.callsign) - 1);
    }
    out.callsignIsPlaceholder = true;
  }

  // ---- airline ----
  if (ac.hasAirlineName && ac.airlineName[0] != '\0') {
    out.hasAirline = true;
    std::strncpy(out.airlineName, ac.airlineName, sizeof(out.airlineName) - 1);
  }

  // ---- aircraft type ----
  if (ac.hasAircraftType && ac.aircraftTypeCode[0] != '\0') {
    char typeUpper[sizeof(ac.aircraftTypeCode)];
    std::strncpy(typeUpper, ac.aircraftTypeCode, sizeof(typeUpper) - 1);
    typeUpper[sizeof(typeUpper) - 1] = '\0';
    toUpper(typeUpper);
    std::strncpy(out.aircraftType, typeUpper, sizeof(out.aircraftType) - 1);
  } else {
    std::strncpy(out.aircraftType, "\xE2\x80\x94", sizeof(out.aircraftType) - 1);
  }

  // ---- ICAO ----
  if (ac.icaoHex[0] != '\0') {
    char icaoUpper[sizeof(ac.icaoHex)];
    std::strncpy(icaoUpper, ac.icaoHex, sizeof(icaoUpper) - 1);
    icaoUpper[sizeof(icaoUpper) - 1] = '\0';
    toUpper(icaoUpper);
    std::strncpy(out.icao, icaoUpper, sizeof(out.icao) - 1);
  } else {
    std::strncpy(out.icao, "\xE2\x80\x94", sizeof(out.icao) - 1);
  }

  // ---- distance (km -> NM) ----
  if (ac.hasDistanceFromObserverKm) {
    out.hasDistance = true;
    const double nm = ac.distanceFromObserverKm * kKmToNm;
    std::snprintf(out.distanceValue, sizeof(out.distanceValue), "%.1f", nm);
  }

  // ---- altitude ----
  if (ac.onGround) {
    out.hasAltitude = true;
    out.altitudeIsGround = true;
    std::strncpy(out.altitudeValue, "GROUND", sizeof(out.altitudeValue) - 1);
  } else if (ac.hasAltitudeFt) {
    out.hasAltitude = true;
    formatIntWithCommas(std::lround(ac.altitudeFt), out.altitudeValue, sizeof(out.altitudeValue));
  }

  // ---- speed ----
  if (ac.hasGroundSpeedKt) {
    out.hasSpeed = true;
    std::snprintf(out.speedValue, sizeof(out.speedValue), "%.0f", ac.groundSpeedKt);
  }

  // ---- track ----
  if (ac.hasTrackHeadingDeg) {
    out.hasTrack = true;
    int deg = static_cast<int>(std::lround(ac.trackHeadingDeg)) % 360;
    if (deg < 0) deg += 360;
    std::snprintf(out.trackDegrees, sizeof(out.trackDegrees), "%d", deg);
    std::strncpy(out.trackCompass, compassFromDegrees(static_cast<double>(deg)), sizeof(out.trackCompass) - 1);
  }

  // ---- squawk ----
  if (ac.hasSquawk && ac.squawk[0] != '\0') {
    out.hasSquawk = true;
    std::strncpy(out.squawk, ac.squawk, sizeof(out.squawk) - 1);
  } else {
    std::strncpy(out.squawk, "\xE2\x80\x94", sizeof(out.squawk) - 1);
  }

  // ---- position -- always known, lat/lon are plain doubles ----
  std::snprintf(out.position, sizeof(out.position), "%.4f, %.4f", ac.latitude, ac.longitude);

  // ---- bearing from observer (which way to look) ----
  if (ac.hasBearingFromObserverDeg) {
    out.hasBearing = true;
    int deg = static_cast<int>(std::lround(ac.bearingFromObserverDeg)) % 360;
    if (deg < 0) deg += 360;
    std::snprintf(out.bearingDegrees, sizeof(out.bearingDegrees), "%d", deg);
    std::strncpy(out.bearingCompass, compassFromDegrees(static_cast<double>(deg)), sizeof(out.bearingCompass) - 1);
  }

  // ---- vertical rate ----
  if (ac.hasVerticalRateFtPerMin) {
    out.hasVerticalRate = true;
    const long rounded = std::lround(ac.verticalRateFtPerMin);
    if (rounded > 0) {
      char mag[10];
      formatIntWithCommas(rounded, mag, sizeof(mag));
      std::snprintf(out.verticalRateValue, sizeof(out.verticalRateValue), "+%s", mag);
    } else {
      formatIntWithCommas(rounded, out.verticalRateValue, sizeof(out.verticalRateValue));
    }
  }

  // ---- status (emergency > stale > motion) ----
  if (ac.emergencyState != EmergencyState::None) {
    out.status = DisplayStatus::Emergency;
  } else if (stale) {
    out.status = DisplayStatus::Stale;
  } else {
    switch (classifyMotionStatus(ac.onGround, ac.hasVerticalRateFtPerMin, ac.verticalRateFtPerMin)) {
      case MotionStatus::Ground: out.status = DisplayStatus::Ground; break;
      case MotionStatus::Climb: out.status = DisplayStatus::Climb; break;
      case MotionStatus::Descent: out.status = DisplayStatus::Descent; break;
      case MotionStatus::Level: out.status = DisplayStatus::Level; break;
      case MotionStatus::Airborne: out.status = DisplayStatus::Airborne; break;
    }
  }
}

void buildBatteryViewModel(const BatteryState& battery, BatteryViewModel& out) {
  out = BatteryViewModel{};
  out.known = battery.valid;
  out.charging = battery.charging;

  if (!battery.valid) {
    std::strncpy(out.percentText, "\xE2\x80\x94", sizeof(out.percentText) - 1);
    out.colorRole = StatusColorRole::Neutral;
    return;
  }

  out.percent = battery.percent > 100 ? 100 : battery.percent;
  std::snprintf(out.percentText, sizeof(out.percentText), "%u%%", out.percent);

  if (out.percent < 10) {
    out.colorRole = StatusColorRole::Critical;
  } else if (out.percent < 20) {
    out.colorRole = StatusColorRole::Caution;
  } else {
    out.colorRole = StatusColorRole::Good;
  }
}

void formatDataAge(uint32_t ageSeconds, bool stale, char* out, size_t outLen) {
  if (stale) {
    std::snprintf(out, outLen, "STALE");
    return;
  }
  if (ageSeconds * 1000ULL <= kStalePositionThresholdMs / 4) {
    std::snprintf(out, outLen, "LIVE");
    return;
  }
  std::snprintf(out, outLen, "%us", static_cast<unsigned>(ageSeconds));
}

}  // namespace ofd
