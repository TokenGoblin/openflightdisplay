#pragma once

#include <cstddef>
#include <cstdint>

namespace ofd {

constexpr size_t kMaxAircraftPerUpdate = 10;

enum class EmergencyState : uint8_t {
  None,
  General,
  Medical,
  MinimumFuel,
  NoCommunications,
  UnlawfulInterference,
  Downed,
};

// Full aircraft state with all available ADS‑B fields.
// Fixed-size buffers only — no heap allocation.
struct AircraftState {
  char icaoHex[7] = {0};
  bool hasCallsign = false;
  char callsign[17] = {0};

  // Resolved airline info (populated during parsing)
  bool hasAirlineName = false;
  char airlineName[40] = {0};
  char airlineIcao[4] = {0};

  // Aircraft type code (e.g. "B738", "A320") from adsb.lol field `t`
  bool hasAircraftType = false;
  char aircraftTypeCode[8] = {0};

  double latitude = 0.0;
  double longitude = 0.0;

  bool hasAltitudeFt = false;
  double altitudeFt = 0.0;

  bool hasGroundSpeedKt = false;
  double groundSpeedKt = 0.0;

  // Computed once during parsing: speedKt × 1.15078
  bool hasGroundSpeedMph = false;
  double groundSpeedMph = 0.0;

  // Track/heading from adsb.lol field `track`
  bool hasTrackHeadingDeg = false;
  double trackHeadingDeg = 0.0;

  bool hasVerticalRateFtPerMin = false;
  double verticalRateFtPerMin = 0.0;

  bool hasSquawk = false;
  char squawk[5] = {0};

  bool onGround = false;
  EmergencyState emergencyState = EmergencyState::None;

  // Computed by ranking (distance/bearing from observer location)
  bool hasDistanceFromObserverKm = false;
  double distanceFromObserverKm = 0.0;
  bool hasBearingFromObserverDeg = false;
  double bearingFromObserverDeg = 0.0;

  // Unix epoch ms — set to time(nullptr)*1000 by the provider
  int64_t positionTimestampMs = 0;
};

struct AircraftList {
  AircraftState items[kMaxAircraftPerUpdate];
  size_t count = 0;
};

}  // namespace ofd