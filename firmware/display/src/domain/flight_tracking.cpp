#include "domain/flight_tracking.h"

#include <cstdio>
#include <cstring>

#include "domain/airline.h"
#include "domain/geo.h"

namespace ofd {

namespace {

constexpr double kKnotsToKmh = 1.852;

bool isDigit(char c) { return c >= '0' && c <= '9'; }
bool isAlpha(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); }
char toUpper(char c) { return (c >= 'a' && c <= 'z') ? static_cast<char>(c - 'a' + 'A') : c; }

}  // namespace

// ---- identifiers ----

void trimCallsign(const char* raw, char* out, size_t outLen) {
  if (out == nullptr || outLen == 0) return;
  out[0] = '\0';
  if (raw == nullptr) return;

  size_t begin = 0;
  while (raw[begin] == ' ') begin++;

  size_t end = std::strlen(raw);
  while (end > begin && raw[end - 1] == ' ') end--;

  size_t n = end - begin;
  if (n > outLen - 1) n = outLen - 1;
  std::memcpy(out, raw + begin, n);
  out[n] = '\0';
}

bool normalizeFlightIdentifier(const char* input, char* out, size_t outLen) {
  if (out == nullptr || outLen < 4) return false;
  out[0] = '\0';
  if (input == nullptr) return false;

  // Strip everything that isn't alphanumeric and uppercase the rest, so
  // "ua 1234", "UA-1234" and "ua1234" all land in the same place.
  char compact[16];
  size_t n = 0;
  for (size_t i = 0; input[i] != '\0' && n < sizeof(compact) - 1; i++) {
    const char c = input[i];
    if (isAlpha(c) || isDigit(c)) compact[n++] = toUpper(c);
  }
  compact[n] = '\0';
  if (n < 3) return false;

  // Split the leading letters from the numeric part. A flight identifier
  // without digits isn't one -- better to reject it than to go looking
  // for an aircraft that can't exist.
  size_t letters = 0;
  while (compact[letters] != '\0' && isAlpha(compact[letters])) letters++;
  if (letters == 0 || compact[letters] == '\0') return false;

  const char* digits = compact + letters;

  // Two-letter prefix: an IATA code that needs expanding to the ICAO
  // designator ADS-B actually broadcasts. Unrecognised codes fall
  // through unchanged rather than being mangled -- somebody tracking a
  // carrier that isn't in our table should still get a literal match
  // attempt instead of a rewrite into nonsense.
  if (letters == 2) {
    char iata[3] = {compact[0], compact[1], '\0'};
    const char* icao = icaoForIataAirline(iata);
    if (icao != nullptr) {
      if (std::strlen(icao) + std::strlen(digits) >= outLen) return false;
      std::snprintf(out, outLen, "%s%s", icao, digits);
      return true;
    }
  }

  if (n >= outLen) return false;
  std::strcpy(out, compact);
  return true;
}

// ---- progress ----

const char* flightPhaseWord(FlightPhase phase) {
  switch (phase) {
    case FlightPhase::AwaitingContact: return "WAITING";
    case FlightPhase::Enroute:         return "ENROUTE";
    case FlightPhase::Descending:      return "DESCENDING";
    case FlightPhase::Approaching:     return "APPROACHING";
    case FlightPhase::Landed:          return "LANDED";
    case FlightPhase::LostContact:     return "NO CONTACT";
  }
  return "WAITING";
}

FlightProgress computeFlightProgress(const AircraftState& aircraft, const Airport& destination,
                                     bool everSeen, uint32_t secondsSinceContact) {
  FlightProgress p;
  p.secondsSinceContact = secondsSinceContact;

  // Never seen: the transponder isn't on yet (or the identifier is
  // wrong). Either way there is nothing to compute, and pretending
  // otherwise would put a fabricated ETA on screen.
  if (!everSeen) {
    p.phase = FlightPhase::AwaitingContact;
    return p;
  }

  const bool haveDestination = destination.valid;
  if (haveDestination) {
    p.hasDistance = true;
    p.distanceToDestinationKm = haversineDistanceKm(aircraft.latitude, aircraft.longitude,
                                                    destination.latitude, destination.longitude);
  }

  // Touchdown, judged conservatively: near the field AND slow AND close
  // to field elevation. Any single one of those alone is a false
  // positive waiting to happen -- a low overflight, a go-around, or a
  // barometric glitch. Height is measured against the destination's own
  // elevation, so this works at Denver as well as at sea level.
  if (haveDestination && p.distanceToDestinationKm <= kLandedRadiusKm) {
    const double heightAboveField =
        aircraft.hasAltitudeFt ? aircraft.altitudeFt - destination.elevationFt : 0.0;
    const bool lowEnough = aircraft.onGround || (aircraft.hasAltitudeFt && heightAboveField <= kLandedMaxHeightFt);
    const bool slowEnough =
        aircraft.onGround || (aircraft.hasGroundSpeedKt && aircraft.groundSpeedKt <= kLandedMaxGroundSpeedKt);
    if (lowEnough && slowEnough) {
      p.phase = FlightPhase::Landed;
      return p;
    }
  }

  // Silent for a while and not at the destination: a coverage gap, not
  // an arrival. Reported as its own state so nobody reads a frozen
  // position as a landing and leaves early.
  if (secondsSinceContact >= kLostContactSeconds) {
    p.phase = FlightPhase::LostContact;
    return p;
  }

  if (haveDestination && p.distanceToDestinationKm <= kApproachRadiusKm) {
    p.phase = FlightPhase::Approaching;
  } else if (aircraft.hasVerticalRateFtPerMin && aircraft.verticalRateFtPerMin <= kDescentRateFtPerMin) {
    p.phase = FlightPhase::Descending;
  } else {
    p.phase = FlightPhase::Enroute;
  }

  // ETA is distance over current groundspeed -- a straight-line estimate
  // that ignores routing, holding and taxi time, and says so by being
  // labelled "at current speed" everywhere it's shown. An aircraft
  // stopped at the gate has a distance but no usable ETA, hence the
  // speed floor rather than a division that would produce infinity.
  if (haveDestination && aircraft.hasGroundSpeedKt && aircraft.groundSpeedKt > 1.0) {
    const double hours = p.distanceToDestinationKm / (aircraft.groundSpeedKt * kKnotsToKmh);
    const double minutes = hours * 60.0;
    // Cap rather than overflow: anything beyond a day out is a data
    // problem, not a flight worth counting down to.
    if (minutes >= 0.0 && minutes < 1440.0) {
      p.hasEta = true;
      p.minutesRemaining = static_cast<uint32_t>(minutes + 0.5);
    }
  }

  return p;
}

// ---- formatting ----

void formatMinutesRemaining(bool hasEta, uint32_t minutes, char* out, size_t outLen) {
  if (out == nullptr || outLen < 8) return;
  if (!hasEta) {
    std::snprintf(out, outLen, "\xE2\x80\x94");  // em dash
    return;
  }
  if (minutes < 60) {
    std::snprintf(out, outLen, "%u", static_cast<unsigned>(minutes));
    return;
  }
  std::snprintf(out, outLen, "%uH%02u", static_cast<unsigned>(minutes / 60),
                static_cast<unsigned>(minutes % 60));
}

// ---- polling cadence ----

uint32_t pollIntervalMsFor(const FlightProgress& progress) {
  switch (progress.phase) {
    case FlightPhase::Landed:
      // Nothing further to learn; the caller stops polling entirely, but
      // return the ceiling rather than zero so a caller that keeps going
      // does so as slowly as possible.
      return kMaxPollIntervalMs;

    case FlightPhase::AwaitingContact:
      // Waiting for a transponder to come alive. Two minutes is
      // responsive enough to catch an early pushback without hammering a
      // free API for a flight that may be hours away.
      return 120000;

    case FlightPhase::LostContact:
      // Something is already wrong; polling harder won't fix a coverage
      // gap, and the aircraft usually reappears on its own.
      return 60000;

    case FlightPhase::Approaching:
      return kMinPollIntervalMs;

    case FlightPhase::Enroute:
    case FlightPhase::Descending:
      break;
  }

  if (!progress.hasEta) return 60000;

  const uint32_t mins = progress.minutesRemaining;
  if (mins > 90) return kMaxPollIntervalMs;  // 5 min
  if (mins > 30) return 120000;
  if (mins > 10) return 60000;
  if (mins > 3) return 20000;
  return kMinPollIntervalMs;
}

}  // namespace ofd
