#pragma once

#include <cstddef>
#include <cstdint>

#include "domain/aircraft.h"
#include "domain/battery.h"

namespace ofd {

// Pure data -> display-string normalization for the Core2's airport-style
// flight-information screen. No Arduino/M5GFX dependency here on purpose --
// this is the one part of the UI where formatting bugs (a wrong sign, a
// stray "nan", a fallback that doesn't fit its field) are easy to get wrong
// and easy to unit test, so it's kept in domain/ and covered by
// test/native/test_display_format like the rest of the domain layer. See
// docs/DISPLAY_UI.md for the full design rationale.

// ---- normalized motion / operational status ----

enum class MotionStatus : uint8_t { Ground, Climb, Descent, Level, Airborne };

// What the STATUS grid cell shows, in priority order (checked top to
// bottom): an active emergency always wins, then staleness, then the
// aircraft's actual phase of flight. Kept distinct from MotionStatus so
// "the aircraft is climbing" and "the data describing it is stale" can
// both be represented without one silently overwriting the other.
enum class DisplayStatus : uint8_t { Ground, Climb, Descent, Level, Airborne, Stale, Emergency };

// Color intent, not a concrete RGB565 value -- ui_theme.h maps these to
// the actual palette so domain/ stays dependency-free.
enum class StatusColorRole : uint8_t { Neutral, Good, Caution, Critical };

const char* motionStatusWord(MotionStatus status);
const char* displayStatusWord(DisplayStatus status);
StatusColorRole displayStatusColorRole(DisplayStatus status);

// Derives phase-of-flight from on-ground + vertical-rate, without regard
// to staleness or emergency (those are layered on top by the caller).
MotionStatus classifyMotionStatus(bool onGround, bool hasVerticalRateFtPerMin, double verticalRateFtPerMin);

// ---- aircraft view model ----

// All fields are pre-formatted, fixed-size, and render-ready -- the
// drawing code should never need to printf, concatenate, or branch on
// raw AircraftState fields. A field with has*=false still contains a
// short, safe fallback string (never empty, "nan", or a raw sentinel).
struct AircraftViewModel {
  char callsign[18] = {0};
  bool callsignIsPlaceholder = false;  // true => dim/secondary treatment

  bool hasAirline = false;
  char airlineName[40] = {0};

  char aircraftType[10] = {0};  // "B738" or "TYPE \xE2\x80\x94" (em dash)

  char icao[8] = {0};  // "A1B2C3" or "\xE2\x80\x94"

  bool hasDistance = false;
  char distanceValue[10] = {0};  // "6.8", "99.9"
  static constexpr const char* kDistanceUnit = "NM";

  bool hasAltitude = false;
  bool altitudeIsGround = false;
  char altitudeValue[10] = {0};  // "12,450" (thousands separator) or "GROUND"
  static constexpr const char* kAltitudeUnit = "FT";

  bool hasSpeed = false;
  char speedValue[8] = {0};  // "286"
  static constexpr const char* kSpeedUnit = "KT";

  bool hasTrack = false;
  char trackDegrees[5] = {0};  // "247" -- renderer adds the degree mark
  char trackCompass[4] = {0};  // "WSW"

  bool hasVerticalRate = false;
  char verticalRateValue[10] = {0};  // "+1,250", "-850", "0"
  static constexpr const char* kVerticalRateUnit = "FT/MIN";

  bool hasSquawk = false;
  char squawk[6] = {0};  // "7000" or "\xE2\x80\x94"

  // Always known (raw lat/lon are plain doubles, not has*-gated) --
  // formatted signed decimal degrees, e.g. "47.6062, -122.3321".
  char position[24] = {0};

  // Bearing *from the observer to the aircraft* -- i.e. which way to
  // physically look to find it -- as distinct from `trackDegrees` above,
  // which is the aircraft's own direction of travel. Only the Detail
  // page shows this; it's genuinely different information from track,
  // not a duplicate.
  bool hasBearing = false;
  char bearingDegrees[5] = {0};
  char bearingCompass[4] = {0};

  DisplayStatus status = DisplayStatus::Airborne;

  bool stale = false;
  uint32_t ageSeconds = 0;
};

// Builds the full view model from raw domain state. `stale` should come
// from the caller's own staleness check (see domain/staleness.h) -- when
// true, `status` is forced to DisplayStatus::Stale regardless of the
// aircraft's actual motion, but every other field still reflects the
// last known values (never blanked), per docs/DISPLAY_UI.md's
// "preserve, don't hide" staleness rule.
void buildAircraftViewModel(const AircraftState& aircraft, uint32_t ageSeconds, bool stale,
                             AircraftViewModel& out);

// ---- battery view model ----

struct BatteryViewModel {
  bool known = false;
  uint8_t percent = 0;
  char percentText[6] = {0};  // "100%", "—" when unknown
  bool charging = false;
  StatusColorRole colorRole = StatusColorRole::Neutral;
};

void buildBatteryViewModel(const BatteryState& battery, BatteryViewModel& out);

// ---- data freshness ----

// Short, header-safe freshness caption: "LIVE" while very fresh, the
// exact age in seconds while aging but still trustworthy, "STALE" past
// kStalePositionThresholdMs (domain/staleness.h). `out` must be at least
// 6 bytes.
void formatDataAge(uint32_t ageSeconds, bool stale, char* out, size_t outLen);

}  // namespace ofd
