#include <unity.h>

#include <cstring>

#include "domain/flight_tracking.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

namespace {

// KSEA, from adsb.lol's /api/0/airport/KSEA -- a real field with a real
// elevation, so the landed check is exercised against something other
// than sea level.
Airport seatac() {
  Airport a;
  a.valid = true;
  std::strcpy(a.icao, "KSEA");
  std::strcpy(a.iata, "SEA");
  std::strcpy(a.name, "Seattle Tacoma International Airport");
  a.latitude = 47.449001;
  a.longitude = -122.308998;
  a.elevationFt = 433.0;
  return a;
}

// An aircraft roughly 300 km south of KSEA at cruise.
AircraftState enrouteAircraft() {
  AircraftState ac;
  std::strcpy(ac.icaoHex, "a1b2c3");
  ac.hasCallsign = true;
  std::strcpy(ac.callsign, "ASA123");
  ac.latitude = 44.75;
  ac.longitude = -122.308998;
  ac.hasAltitudeFt = true;
  ac.altitudeFt = 35000.0;
  ac.hasGroundSpeedKt = true;
  ac.groundSpeedKt = 450.0;
  return ac;
}

}  // namespace

// ---- callsign padding ----

void test_trims_adsb_callsign_padding() {
  char out[16];
  trimCallsign("BAW249  ", out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("BAW249", out);
}

void test_trim_handles_no_padding_and_null() {
  char out[16];
  trimCallsign("UAL1234", out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("UAL1234", out);

  trimCallsign(nullptr, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("", out);

  trimCallsign("        ", out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("", out);
}

void test_trim_truncates_rather_than_overflows() {
  char out[4];
  trimCallsign("BAW249  ", out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("BAW", out);
}

// ---- flight number normalization ----

void test_expands_iata_flight_number_to_icao_callsign() {
  char out[16];
  TEST_ASSERT_TRUE(normalizeFlightIdentifier("UA1234", out, sizeof(out)));
  TEST_ASSERT_EQUAL_STRING("UAL1234", out);
}

void test_normalizes_case_and_separators() {
  char out[16];
  TEST_ASSERT_TRUE(normalizeFlightIdentifier("ua 1234", out, sizeof(out)));
  TEST_ASSERT_EQUAL_STRING("UAL1234", out);

  TEST_ASSERT_TRUE(normalizeFlightIdentifier("ba-249", out, sizeof(out)));
  TEST_ASSERT_EQUAL_STRING("BAW249", out);
}

void test_passes_through_icao_callsign_unchanged() {
  char out[16];
  TEST_ASSERT_TRUE(normalizeFlightIdentifier("UAL1234", out, sizeof(out)));
  TEST_ASSERT_EQUAL_STRING("UAL1234", out);
}

// An airline we don't have in the table must still be trackable -- the
// user may well have typed the exact callsign ADS-B is broadcasting.
void test_unknown_two_letter_prefix_is_not_mangled() {
  char out[16];
  TEST_ASSERT_TRUE(normalizeFlightIdentifier("zz999", out, sizeof(out)));
  TEST_ASSERT_EQUAL_STRING("ZZ999", out);
}

void test_rejects_identifiers_without_digits() {
  char out[16];
  TEST_ASSERT_FALSE(normalizeFlightIdentifier("UNITED", out, sizeof(out)));
  TEST_ASSERT_FALSE(normalizeFlightIdentifier("", out, sizeof(out)));
  TEST_ASSERT_FALSE(normalizeFlightIdentifier(nullptr, out, sizeof(out)));
}

void test_rejects_identifiers_without_letters() {
  char out[16];
  TEST_ASSERT_FALSE(normalizeFlightIdentifier("1234", out, sizeof(out)));
}

// ---- phase: awaiting contact ----

// The flight hasn't pushed back. This is a normal state, not an error,
// and it must not invent an ETA.
void test_never_seen_is_awaiting_contact_with_no_eta() {
  const FlightProgress p = computeFlightProgress(enrouteAircraft(), seatac(), /*everSeen=*/false, 0);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::AwaitingContact), static_cast<int>(p.phase));
  TEST_ASSERT_FALSE(p.hasEta);
  TEST_ASSERT_FALSE(p.hasDistance);
}

// ---- phase: enroute / descending / approaching ----

void test_enroute_far_out_computes_eta_from_groundspeed() {
  const FlightProgress p = computeFlightProgress(enrouteAircraft(), seatac(), true, 5);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Enroute), static_cast<int>(p.phase));
  TEST_ASSERT_TRUE(p.hasDistance);
  TEST_ASSERT_TRUE(p.hasEta);
  // ~300 km at 450 kt (833 km/h) is a little over 21 minutes.
  TEST_ASSERT_TRUE(p.minutesRemaining > 15 && p.minutesRemaining < 30);
}

void test_descending_when_vertical_rate_is_negative() {
  AircraftState ac = enrouteAircraft();
  ac.hasVerticalRateFtPerMin = true;
  ac.verticalRateFtPerMin = -1800.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Descending), static_cast<int>(p.phase));
}

// Proximity outranks vertical rate: an aircraft 30 km out levelling off
// on final is Approaching, not Enroute.
void test_near_destination_is_approaching_even_when_level() {
  AircraftState ac = enrouteAircraft();
  ac.latitude = 47.15;  // ~33 km south of KSEA
  ac.altitudeFt = 4000.0;
  ac.groundSpeedKt = 210.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Approaching), static_cast<int>(p.phase));
}

// ---- phase: landed ----

void test_on_ground_at_destination_is_landed() {
  AircraftState ac = enrouteAircraft();
  ac.latitude = 47.449001;
  ac.longitude = -122.308998;
  ac.onGround = true;
  ac.hasAltitudeFt = true;
  ac.altitudeFt = 433.0;
  ac.groundSpeedKt = 15.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Landed), static_cast<int>(p.phase));
}

// Height is measured against field elevation, not sea level. At KSEA
// (433 ft) an aircraft at 800 ft is only ~367 ft above the field and has
// landed; the same 800 ft reading at a sea-level field would not be.
void test_landed_measures_height_above_field_not_sea_level() {
  AircraftState ac = enrouteAircraft();
  ac.latitude = 47.449001;
  ac.longitude = -122.308998;
  ac.altitudeFt = 800.0;
  ac.groundSpeedKt = 90.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Landed), static_cast<int>(p.phase));
}

// A fast, high overflight of the destination is not a landing. Without
// the speed and height conditions this is exactly the false positive
// that would send somebody to arrivals far too early.
void test_fast_high_overflight_of_destination_is_not_landed() {
  AircraftState ac = enrouteAircraft();
  ac.latitude = 47.449001;
  ac.longitude = -122.308998;
  ac.altitudeFt = 35000.0;
  ac.groundSpeedKt = 450.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_NOT_EQUAL(static_cast<int>(FlightPhase::Landed), static_cast<int>(p.phase));
}

// ---- phase: lost contact ----

void test_long_silence_away_from_destination_is_lost_contact() {
  const FlightProgress p =
      computeFlightProgress(enrouteAircraft(), seatac(), true, kLostContactSeconds + 1);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::LostContact), static_cast<int>(p.phase));
}

// Landing wins over silence: an aircraft that went quiet *at the field*
// has arrived, and must not be reported as a coverage gap.
void test_landed_takes_precedence_over_lost_contact() {
  AircraftState ac = enrouteAircraft();
  ac.latitude = 47.449001;
  ac.longitude = -122.308998;
  ac.onGround = true;
  ac.altitudeFt = 433.0;
  ac.groundSpeedKt = 10.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, kLostContactSeconds + 60);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Landed), static_cast<int>(p.phase));
}

// ---- ETA edge cases ----

// An aircraft parked at the gate has a distance but no meaningful ETA;
// dividing by its groundspeed must not produce an infinity.
void test_stationary_aircraft_has_distance_but_no_eta() {
  AircraftState ac = enrouteAircraft();
  ac.hasGroundSpeedKt = true;
  ac.groundSpeedKt = 0.0;
  const FlightProgress p = computeFlightProgress(ac, seatac(), true, 5);
  TEST_ASSERT_TRUE(p.hasDistance);
  TEST_ASSERT_FALSE(p.hasEta);
}

void test_no_destination_yields_no_distance_or_eta() {
  Airport unresolved;  // valid == false
  const FlightProgress p = computeFlightProgress(enrouteAircraft(), unresolved, true, 5);
  TEST_ASSERT_FALSE(p.hasDistance);
  TEST_ASSERT_FALSE(p.hasEta);
  TEST_ASSERT_EQUAL(static_cast<int>(FlightPhase::Enroute), static_cast<int>(p.phase));
}

// ---- adaptive polling ----

// The efficiency claim, asserted rather than assumed: the interval must
// shorten monotonically as the flight gets closer.
void test_poll_interval_tightens_as_arrival_approaches() {
  FlightProgress p;
  p.phase = FlightPhase::Enroute;
  p.hasEta = true;

  p.minutesRemaining = 120;
  const uint32_t farOut = pollIntervalMsFor(p);
  p.minutesRemaining = 45;
  const uint32_t midway = pollIntervalMsFor(p);
  p.minutesRemaining = 20;
  const uint32_t closing = pollIntervalMsFor(p);
  p.minutesRemaining = 5;
  const uint32_t nearly = pollIntervalMsFor(p);
  p.minutesRemaining = 1;
  const uint32_t imminent = pollIntervalMsFor(p);

  TEST_ASSERT_TRUE(farOut > midway);
  TEST_ASSERT_TRUE(midway > closing);
  TEST_ASSERT_TRUE(closing > nearly);
  TEST_ASSERT_TRUE(nearly > imminent);
  TEST_ASSERT_EQUAL_UINT32(kMaxPollIntervalMs, farOut);
  TEST_ASSERT_EQUAL_UINT32(kMinPollIntervalMs, imminent);
}

void test_poll_interval_respects_bounds_in_every_phase() {
  const FlightPhase phases[] = {FlightPhase::AwaitingContact, FlightPhase::Enroute,
                                FlightPhase::Descending,      FlightPhase::Approaching,
                                FlightPhase::Landed,          FlightPhase::LostContact};
  for (const FlightPhase phase : phases) {
    FlightProgress p;
    p.phase = phase;
    const uint32_t interval = pollIntervalMsFor(p);
    TEST_ASSERT_TRUE(interval >= kMinPollIntervalMs);
    TEST_ASSERT_TRUE(interval <= kMaxPollIntervalMs);
  }
}

void test_approaching_polls_at_the_fastest_rate() {
  FlightProgress p;
  p.phase = FlightPhase::Approaching;
  TEST_ASSERT_EQUAL_UINT32(kMinPollIntervalMs, pollIntervalMsFor(p));
}

// Without an ETA there is nothing to ramp against, so it must fall back
// to a sane middle rather than either extreme.
void test_missing_eta_falls_back_to_a_moderate_interval() {
  FlightProgress p;
  p.phase = FlightPhase::Enroute;
  p.hasEta = false;
  const uint32_t interval = pollIntervalMsFor(p);
  TEST_ASSERT_TRUE(interval > kMinPollIntervalMs);
  TEST_ASSERT_TRUE(interval < kMaxPollIntervalMs);
}

// ---- countdown formatting ----

void test_formats_minutes_under_an_hour_as_a_bare_number() {
  char out[8];
  formatMinutesRemaining(true, 8, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("8", out);
  formatMinutesRemaining(true, 47, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("47", out);
}

void test_formats_over_an_hour_with_padded_minutes() {
  char out[8];
  formatMinutesRemaining(true, 125, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("2H05", out);
  formatMinutesRemaining(true, 60, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("1H00", out);
}

// "0" would read as "landing now" on a screen somebody is about to
// leave the house on the strength of.
void test_no_eta_formats_as_a_dash_not_zero() {
  char out[8];
  formatMinutesRemaining(false, 0, out, sizeof(out));
  TEST_ASSERT_EQUAL_STRING("\xE2\x80\x94", out);
}

int main(int argc, char** argv) {
  (void)argc;
  (void)argv;
  UNITY_BEGIN();

  RUN_TEST(test_trims_adsb_callsign_padding);
  RUN_TEST(test_trim_handles_no_padding_and_null);
  RUN_TEST(test_trim_truncates_rather_than_overflows);

  RUN_TEST(test_expands_iata_flight_number_to_icao_callsign);
  RUN_TEST(test_normalizes_case_and_separators);
  RUN_TEST(test_passes_through_icao_callsign_unchanged);
  RUN_TEST(test_unknown_two_letter_prefix_is_not_mangled);
  RUN_TEST(test_rejects_identifiers_without_digits);
  RUN_TEST(test_rejects_identifiers_without_letters);

  RUN_TEST(test_never_seen_is_awaiting_contact_with_no_eta);
  RUN_TEST(test_enroute_far_out_computes_eta_from_groundspeed);
  RUN_TEST(test_descending_when_vertical_rate_is_negative);
  RUN_TEST(test_near_destination_is_approaching_even_when_level);

  RUN_TEST(test_on_ground_at_destination_is_landed);
  RUN_TEST(test_landed_measures_height_above_field_not_sea_level);
  RUN_TEST(test_fast_high_overflight_of_destination_is_not_landed);

  RUN_TEST(test_long_silence_away_from_destination_is_lost_contact);
  RUN_TEST(test_landed_takes_precedence_over_lost_contact);

  RUN_TEST(test_stationary_aircraft_has_distance_but_no_eta);
  RUN_TEST(test_no_destination_yields_no_distance_or_eta);

  RUN_TEST(test_poll_interval_tightens_as_arrival_approaches);
  RUN_TEST(test_poll_interval_respects_bounds_in_every_phase);
  RUN_TEST(test_approaching_polls_at_the_fastest_rate);
  RUN_TEST(test_missing_eta_falls_back_to_a_moderate_interval);

  RUN_TEST(test_formats_minutes_under_an_hour_as_a_bare_number);
  RUN_TEST(test_formats_over_an_hour_with_padded_minutes);
  RUN_TEST(test_no_eta_formats_as_a_dash_not_zero);

  return UNITY_END();
}
