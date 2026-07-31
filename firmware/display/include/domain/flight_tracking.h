#pragma once

#include <cstddef>
#include <cstdint>

#include "domain/aircraft.h"

namespace ofd {

// Following one specific flight to its destination -- the "am I leaving
// for the airport at the right time" use case.
//
// Everything here is pure computation over a position report and a
// destination, with no Arduino/network dependency, so the parts that are
// easy to get quietly wrong (a callsign that never matches, an ETA that
// divides by zero, a "landed" state that fires over the threshold at
// 3,000 ft) are covered by test/native/test_flight_tracking rather than
// only being visible on a device while someone waits at arrivals.
//
// What ADS-B can and cannot tell us, because it shapes every type below:
//   - It reports where an aircraft *is*, not where it is *going*. There
//     is no destination in the data, so the user supplies one. adsb.lol's
//     route-inference endpoint (/api/0/routeset) returns an empty 201 for
//     every valid request as of this writing, so it is not an option.
//   - It reports nothing at all before the transponder is on. A flight
//     that hasn't pushed back is indistinguishable from one that doesn't
//     exist -- hence FlightPhase::AwaitingContact, which is a normal
//     state and must never be rendered as an error.
//   - There is no schedule. We can say "arriving in 24 minutes at the
//     current groundspeed"; we cannot say "12 minutes late". Nothing here
//     computes delay against a published timetable, deliberately --
//     see docs/FEATURE_PARITY_MATRIX.md's warning about claiming schedule
//     accuracy from position-only sources.

// ---- identifiers ----

// ADS-B callsigns arrive space-padded to 8 characters ("BAW249  ").
// Trims trailing (and leading) spaces. Safe when src == nullptr.
void trimCallsign(const char* raw, char* out, size_t outLen);

// Normalizes what a human types into the callsign ADS-B actually
// broadcasts, uppercasing and stripping separators along the way:
//
//   "UA1234"   -> "UAL1234"   (IATA airline code expanded to ICAO)
//   "ua 1234"  -> "UAL1234"
//   "UAL1234"  -> "UAL1234"   (already ICAO, passed through)
//   "BA249"    -> "BAW249"
//
// The IATA->ICAO expansion only fires for a recognised 2-character
// airline code (domain/airline.h). An unrecognised prefix is passed
// through uppercased and unmodified rather than mangled -- a user typing
// a callsign we don't have in the table should still be able to track it.
//
// Returns false if the input has no digits or can't fit `out`, which is
// the "that isn't a flight number" case worth reporting to the user
// before they drive to an airport on the strength of it.
bool normalizeFlightIdentifier(const char* input, char* out, size_t outLen);

// ---- destination ----

// A resolved arrival airport. Populated from adsb.lol's
// /api/0/airport/{icao}; `elevationFt` matters because "on the ground"
// is judged against field elevation, not sea level -- Denver's ramp is
// at 5,400 ft.
struct Airport {
  bool valid = false;
  char icao[5] = {0};
  char iata[4] = {0};
  char name[48] = {0};
  double latitude = 0.0;
  double longitude = 0.0;
  double elevationFt = 0.0;
};

// ---- progress ----

enum class FlightPhase : uint8_t {
  // Configured, but the aircraft has never been seen. Normal before
  // pushback; also what a wrong flight number looks like, which is why
  // the UI shows how long it's been waiting.
  AwaitingContact,
  Enroute,
  Descending,
  Approaching,
  Landed,
  // Seen before, now silent, and not near the destination -- an oceanic
  // coverage gap or a lost feeder. Deliberately distinct from Landed:
  // conflating them sends someone to the airport an hour early.
  LostContact,
};

const char* flightPhaseWord(FlightPhase phase);

struct FlightProgress {
  FlightPhase phase = FlightPhase::AwaitingContact;

  // False until there is both a position and a usable groundspeed. A
  // stationary aircraft at the gate has a distance but no meaningful ETA.
  bool hasEta = false;
  uint32_t minutesRemaining = 0;

  bool hasDistance = false;
  double distanceToDestinationKm = 0.0;

  // Seconds since the last position report for this aircraft. Drives the
  // "waiting" caption in AwaitingContact and the staleness treatment
  // everywhere else.
  uint32_t secondsSinceContact = 0;
};

// Thresholds, named rather than inline so the tests and the docs refer to
// the same numbers.
//
// kApproachRadiusKm is deliberately generous (~30 nm): it's the point at
// which "start driving" stops being a rounding error, not a claim about
// the final approach fix.
constexpr double kApproachRadiusKm = 55.0;
// Touchdown is judged conservatively -- all of: near the field, slow, and
// close to field elevation. Any one alone produces false positives (a
// low-and-slow overflight, a go-around, a baro glitch).
constexpr double kLandedRadiusKm = 8.0;
constexpr double kLandedMaxHeightFt = 500.0;
constexpr double kLandedMaxGroundSpeedKt = 120.0;
constexpr double kDescentRateFtPerMin = -300.0;
// Past this with no report, a previously-seen aircraft is LostContact
// rather than silently frozen on screen.
constexpr uint32_t kLostContactSeconds = 300;

// Builds the progress view from the latest position report.
//
// `everSeen` distinguishes "hasn't departed yet" from "we lost it", which
// the aircraft state alone cannot express. When `everSeen` is false the
// aircraft argument is ignored entirely.
FlightProgress computeFlightProgress(const AircraftState& aircraft, const Airport& destination,
                                     bool everSeen, uint32_t secondsSinceContact);

// ---- polling cadence ----

// How long to wait before the next lookup, given where the flight is.
//
// This is the whole efficiency argument for the feature. A flight three
// hours out does not need 15-second polling, and one on short final does.
// Ramping the interval by time-to-arrival cuts request volume by roughly
// 95% against a fixed fast poll while being *more* responsive at the only
// moment anybody is watching. Bounded at both ends: never faster than
// kMinPollIntervalMs (courtesy to a free, community-funded data source
// whose rate limits are dynamic and undocumented), never slower than
// kMaxPollIntervalMs (or a flight that departs early goes unnoticed).
constexpr uint32_t kMinPollIntervalMs = 10000;
constexpr uint32_t kMaxPollIntervalMs = 300000;

uint32_t pollIntervalMsFor(const FlightProgress& progress);

// ---- when to leave ----
//
// The question the countdown exists to answer. Kept as a derived
// property of the tracked flight rather than an entry in a general
// alert-rule engine (packages/shared-models' stubbed AlertRuleSchema):
// there is exactly one of these, it has no cooldown, no channel routing
// and no match expression, and building a rule engine to hold a single
// subtraction would be speculative generality.
//
// The subtraction is not "leave when the aircraft lands". Touchdown is
// not when the person you're collecting walks into the arrivals hall --
// taxi, deplaning, immigration and baggage sit in between, and on a
// long-haul arrival that gap is routinely longer than the drive. Leaving
// it out would send people to the airport to stand around for half an
// hour, which is precisely the failure this feature exists to prevent.
// So both halves are the user's to supply: how long *they* take to get
// there, and how long they expect the walk-out to take.

enum class DepartureAdvice : uint8_t {
  // No ETA yet (flight hasn't been seen, or has no usable groundspeed),
  // or no travel time configured. Nothing honest to say.
  Unknown,
  Wait,
  // Inside the warning window -- put your shoes on.
  LeaveSoon,
  LeaveNow,
  // Departure time has already passed by a margin. Distinct from
  // LeaveNow so the screen can stop escalating and just say so.
  Late,
};

const char* departureAdviceWord(DepartureAdvice advice);

struct DeparturePlan {
  DepartureAdvice advice = DepartureAdvice::Unknown;
  bool hasMinutes = false;
  // Minutes until you should set off. Negative once that moment has
  // passed, which is why it is signed -- clamping at zero would make
  // "leave now" and "you're twenty minutes late" look identical.
  int32_t minutesUntilDeparture = 0;
};

// How far ahead of the leave-now moment to start warning.
constexpr int32_t kLeaveSoonWindowMinutes = 15;
// Past this much overdue, escalating stops helping.
constexpr int32_t kLateThresholdMinutes = 10;

// `travelMinutes` is the user's door-to-arrivals-hall time; zero means
// they haven't configured one, and the result is Unknown rather than a
// guess. `postLandingMinutes` is their estimate of touchdown-to-walk-out.
DeparturePlan computeDeparturePlan(const FlightProgress& progress, uint32_t travelMinutes,
                                   uint32_t postLandingMinutes);

// ---- formatting ----

// Time-to-arrival as something readable at a glance from across a room:
// "8 MIN", "47 MIN", "2H 05M". `out` must be at least 8 bytes. Writes an
// em dash when there is no ETA to show -- never a bare "0", which reads
// as "landing now".
void formatMinutesRemaining(bool hasEta, uint32_t minutes, char* out, size_t outLen);

}  // namespace ofd
