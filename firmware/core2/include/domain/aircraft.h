#pragma once

#include <cstddef>
#include <cstdint>

namespace ofd {

// Bounded, per docs/PROTOCOL.md ("aircraft-update.aircraft is capped at a
// fixed maximum length -- Phase 1: 10").
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

// Deliberately a *subset* of the full AircraftState model in
// packages/shared-models -- only the fields the Phase 1 single-aircraft
// Core2 screen actually renders or reasons about (ranking, staleness).
// Extra fields present in an incoming message are simply ignored during
// parsing (see domain/protocol.cpp), not stored.
struct AircraftState {
  char icaoHex[7] = {0};  // 6 lowercase hex chars + NUL

  bool hasCallsign = false;
  char callsign[17] = {0};

  double latitude = 0.0;
  double longitude = 0.0;

  bool hasAltitudeFt = false;
  double altitudeFt = 0.0;

  bool hasGroundSpeedKt = false;
  double groundSpeedKt = 0.0;

  bool hasVerticalRateFtPerMin = false;
  double verticalRateFtPerMin = 0.0;

  bool onGround = false;
  EmergencyState emergencyState = EmergencyState::None;

  bool hasDistanceFromObserverKm = false;
  double distanceFromObserverKm = 0.0;

  bool hasBearingFromObserverDeg = false;
  double bearingFromObserverDeg = 0.0;

  int64_t positionTimestampMs = 0;
};

struct AircraftList {
  AircraftState items[kMaxAircraftPerUpdate];
  size_t count = 0;
};

}  // namespace ofd
