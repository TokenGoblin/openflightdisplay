#include <unity.h>

#include <cstring>

#include "domain/aircraft.h"
#include "domain/battery.h"
#include "domain/display_format.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

namespace {
AircraftState makeAircraft() {
  AircraftState ac;
  std::strcpy(ac.icaoHex, "A1B2C3");
  return ac;
}
}  // namespace

// ---- callsign ----

void test_callsign_trims_and_uppercases() {
  AircraftState ac = makeAircraft();
  ac.hasCallsign = true;
  std::strcpy(ac.callsign, "ual1234  ");
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("UAL1234", vm.callsign);
  TEST_ASSERT_FALSE(vm.callsignIsPlaceholder);
}

void test_callsign_falls_back_to_icao_when_missing() {
  AircraftState ac = makeAircraft();
  ac.hasCallsign = false;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("A1B2C3", vm.callsign);
  TEST_ASSERT_TRUE(vm.callsignIsPlaceholder);
}

void test_callsign_falls_back_to_no_callsign_when_nothing_known() {
  AircraftState ac;
  ac.hasCallsign = false;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("NO CALLSIGN", vm.callsign);
  TEST_ASSERT_TRUE(vm.callsignIsPlaceholder);
}

void test_callsign_of_all_spaces_treated_as_missing() {
  AircraftState ac = makeAircraft();
  ac.hasCallsign = true;
  std::strcpy(ac.callsign, "        ");
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("A1B2C3", vm.callsign);
  TEST_ASSERT_TRUE(vm.callsignIsPlaceholder);
}

// ---- airline / type / icao ----

void test_airline_omitted_when_not_resolved() {
  AircraftState ac = makeAircraft();
  ac.hasAirlineName = false;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_FALSE(vm.hasAirline);
}

void test_airline_present_when_resolved() {
  AircraftState ac = makeAircraft();
  ac.hasAirlineName = true;
  std::strcpy(ac.airlineName, "United Airlines");
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_TRUE(vm.hasAirline);
  TEST_ASSERT_EQUAL_STRING("United Airlines", vm.airlineName);
}

void test_aircraft_type_missing_shows_em_dash() {
  AircraftState ac = makeAircraft();
  ac.hasAircraftType = false;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("\xE2\x80\x94", vm.aircraftType);
}

void test_aircraft_type_uppercased() {
  AircraftState ac = makeAircraft();
  ac.hasAircraftType = true;
  std::strcpy(ac.aircraftTypeCode, "b738");
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("B738", vm.aircraftType);
}

void test_icao_missing_shows_em_dash() {
  AircraftState ac;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("\xE2\x80\x94", vm.icao);
}

// ---- distance / altitude / speed ----

void test_distance_converts_km_to_nm() {
  AircraftState ac = makeAircraft();
  ac.hasDistanceFromObserverKm = true;
  ac.distanceFromObserverKm = 1.852;  // exactly 1 NM
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_TRUE(vm.hasDistance);
  TEST_ASSERT_EQUAL_STRING("1.0", vm.distanceValue);
}

void test_altitude_ground_overrides_numeric_value() {
  AircraftState ac = makeAircraft();
  ac.onGround = true;
  ac.hasAltitudeFt = true;
  ac.altitudeFt = 50;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_TRUE(vm.altitudeIsGround);
  TEST_ASSERT_EQUAL_STRING("GROUND", vm.altitudeValue);
}

void test_altitude_formats_thousands_separator() {
  AircraftState ac = makeAircraft();
  ac.hasAltitudeFt = true;
  ac.altitudeFt = 12450;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("12,450", vm.altitudeValue);
}

void test_altitude_five_digit_formats_correctly() {
  AircraftState ac = makeAircraft();
  ac.hasAltitudeFt = true;
  ac.altitudeFt = 41000;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("41,000", vm.altitudeValue);
}

void test_altitude_missing_is_flagged() {
  AircraftState ac = makeAircraft();
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_FALSE(vm.hasAltitude);
}

void test_speed_zero_is_shown_not_missing() {
  AircraftState ac = makeAircraft();
  ac.hasGroundSpeedKt = true;
  ac.groundSpeedKt = 0;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_TRUE(vm.hasSpeed);
  TEST_ASSERT_EQUAL_STRING("0", vm.speedValue);
}

// ---- squawk / position / bearing ----

void test_squawk_present() {
  AircraftState ac = makeAircraft();
  ac.hasSquawk = true;
  std::strcpy(ac.squawk, "7000");
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_TRUE(vm.hasSquawk);
  TEST_ASSERT_EQUAL_STRING("7000", vm.squawk);
}

void test_squawk_missing_shows_em_dash() {
  AircraftState ac = makeAircraft();
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_FALSE(vm.hasSquawk);
  TEST_ASSERT_EQUAL_STRING("\xE2\x80\x94", vm.squawk);
}

void test_position_always_formatted() {
  AircraftState ac = makeAircraft();
  ac.latitude = 47.6062;
  ac.longitude = -122.3321;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("47.6062, -122.3321", vm.position);
}

void test_bearing_from_observer_is_distinct_from_track() {
  AircraftState ac = makeAircraft();
  ac.hasTrackHeadingDeg = true;
  ac.trackHeadingDeg = 90;  // aircraft flying east
  ac.hasBearingFromObserverDeg = true;
  ac.bearingFromObserverDeg = 200;  // but it's to the SSW of the observer
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("90", vm.trackDegrees);
  TEST_ASSERT_EQUAL_STRING("200", vm.bearingDegrees);
  TEST_ASSERT_EQUAL_STRING("SSW", vm.bearingCompass);
}

void test_bearing_missing_is_flagged() {
  AircraftState ac = makeAircraft();
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_FALSE(vm.hasBearing);
}

// ---- track ----

void test_track_zero_degrees_is_north() {
  AircraftState ac = makeAircraft();
  ac.hasTrackHeadingDeg = true;
  ac.trackHeadingDeg = 0;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("0", vm.trackDegrees);
  TEST_ASSERT_EQUAL_STRING("N", vm.trackCompass);
}

void test_track_247_is_wsw() {
  AircraftState ac = makeAircraft();
  ac.hasTrackHeadingDeg = true;
  ac.trackHeadingDeg = 247;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("247", vm.trackDegrees);
  TEST_ASSERT_EQUAL_STRING("WSW", vm.trackCompass);
}

void test_track_359_stays_in_range() {
  AircraftState ac = makeAircraft();
  ac.hasTrackHeadingDeg = true;
  ac.trackHeadingDeg = 359.6;  // rounds to 360 -> must wrap to 0
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("0", vm.trackDegrees);
}

// ---- vertical rate ----

void test_vertical_rate_positive_gets_plus_sign() {
  AircraftState ac = makeAircraft();
  ac.hasVerticalRateFtPerMin = true;
  ac.verticalRateFtPerMin = 1250;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("+1,250", vm.verticalRateValue);
}

void test_vertical_rate_negative_gets_minus_sign() {
  AircraftState ac = makeAircraft();
  ac.hasVerticalRateFtPerMin = true;
  ac.verticalRateFtPerMin = -850;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("-850", vm.verticalRateValue);
}

void test_vertical_rate_zero_has_no_sign() {
  AircraftState ac = makeAircraft();
  ac.hasVerticalRateFtPerMin = true;
  ac.verticalRateFtPerMin = 0;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL_STRING("0", vm.verticalRateValue);
}

// ---- status precedence: emergency > stale > motion ----

void test_status_ground() {
  AircraftState ac = makeAircraft();
  ac.onGround = true;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 0, false, vm);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Ground), static_cast<int>(vm.status));
}

void test_status_climb_and_descent_thresholds() {
  AircraftState climb = makeAircraft();
  climb.hasVerticalRateFtPerMin = true;
  climb.verticalRateFtPerMin = 1200;
  AircraftViewModel vmClimb;
  buildAircraftViewModel(climb, 0, false, vmClimb);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Climb), static_cast<int>(vmClimb.status));

  AircraftState descent = makeAircraft();
  descent.hasVerticalRateFtPerMin = true;
  descent.verticalRateFtPerMin = -1200;
  AircraftViewModel vmDescent;
  buildAircraftViewModel(descent, 0, false, vmDescent);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Descent), static_cast<int>(vmDescent.status));

  AircraftState level = makeAircraft();
  level.hasVerticalRateFtPerMin = true;
  level.verticalRateFtPerMin = 50;
  AircraftViewModel vmLevel;
  buildAircraftViewModel(level, 0, false, vmLevel);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Level), static_cast<int>(vmLevel.status));
}

void test_status_stale_overrides_motion() {
  AircraftState ac = makeAircraft();
  ac.onGround = false;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 90, /*stale=*/true, vm);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Stale), static_cast<int>(vm.status));
}

void test_status_emergency_overrides_stale_and_motion() {
  AircraftState ac = makeAircraft();
  ac.emergencyState = EmergencyState::General;
  AircraftViewModel vm;
  buildAircraftViewModel(ac, 90, /*stale=*/true, vm);
  TEST_ASSERT_EQUAL(static_cast<int>(DisplayStatus::Emergency), static_cast<int>(vm.status));
}

// ---- battery ----

void test_battery_unknown_when_invalid() {
  BatteryState b;
  b.valid = false;
  BatteryViewModel vm;
  buildBatteryViewModel(b, vm);
  TEST_ASSERT_FALSE(vm.known);
  TEST_ASSERT_EQUAL(static_cast<int>(StatusColorRole::Neutral), static_cast<int>(vm.colorRole));
}

void test_battery_100_percent_text_fits() {
  BatteryState b;
  b.valid = true;
  b.percent = 100;
  BatteryViewModel vm;
  buildBatteryViewModel(b, vm);
  TEST_ASSERT_EQUAL_STRING("100%", vm.percentText);
  TEST_ASSERT_EQUAL(static_cast<int>(StatusColorRole::Good), static_cast<int>(vm.colorRole));
}

void test_battery_color_tiers() {
  BatteryState b;
  b.valid = true;

  b.percent = 20;
  BatteryViewModel vmGood;
  buildBatteryViewModel(b, vmGood);
  TEST_ASSERT_EQUAL(static_cast<int>(StatusColorRole::Good), static_cast<int>(vmGood.colorRole));

  b.percent = 19;
  BatteryViewModel vmCaution;
  buildBatteryViewModel(b, vmCaution);
  TEST_ASSERT_EQUAL(static_cast<int>(StatusColorRole::Caution), static_cast<int>(vmCaution.colorRole));

  b.percent = 9;
  BatteryViewModel vmCritical;
  buildBatteryViewModel(b, vmCritical);
  TEST_ASSERT_EQUAL(static_cast<int>(StatusColorRole::Critical), static_cast<int>(vmCritical.colorRole));
}

// ---- data freshness ----

void test_data_age_live_within_one_poll_interval() {
  char buf[8];
  formatDataAge(5, false, buf, sizeof(buf));
  TEST_ASSERT_EQUAL_STRING("LIVE", buf);
}

void test_data_age_shows_seconds_while_aging() {
  char buf[8];
  formatDataAge(30, false, buf, sizeof(buf));
  TEST_ASSERT_EQUAL_STRING("30s", buf);
}

void test_data_age_stale_flag_wins_regardless_of_age() {
  char buf[8];
  formatDataAge(2, true, buf, sizeof(buf));
  TEST_ASSERT_EQUAL_STRING("STALE", buf);
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_callsign_trims_and_uppercases);
  RUN_TEST(test_callsign_falls_back_to_icao_when_missing);
  RUN_TEST(test_callsign_falls_back_to_no_callsign_when_nothing_known);
  RUN_TEST(test_callsign_of_all_spaces_treated_as_missing);
  RUN_TEST(test_airline_omitted_when_not_resolved);
  RUN_TEST(test_airline_present_when_resolved);
  RUN_TEST(test_aircraft_type_missing_shows_em_dash);
  RUN_TEST(test_aircraft_type_uppercased);
  RUN_TEST(test_icao_missing_shows_em_dash);
  RUN_TEST(test_distance_converts_km_to_nm);
  RUN_TEST(test_altitude_ground_overrides_numeric_value);
  RUN_TEST(test_altitude_formats_thousands_separator);
  RUN_TEST(test_altitude_five_digit_formats_correctly);
  RUN_TEST(test_altitude_missing_is_flagged);
  RUN_TEST(test_speed_zero_is_shown_not_missing);
  RUN_TEST(test_squawk_present);
  RUN_TEST(test_squawk_missing_shows_em_dash);
  RUN_TEST(test_position_always_formatted);
  RUN_TEST(test_bearing_from_observer_is_distinct_from_track);
  RUN_TEST(test_bearing_missing_is_flagged);
  RUN_TEST(test_track_zero_degrees_is_north);
  RUN_TEST(test_track_247_is_wsw);
  RUN_TEST(test_track_359_stays_in_range);
  RUN_TEST(test_vertical_rate_positive_gets_plus_sign);
  RUN_TEST(test_vertical_rate_negative_gets_minus_sign);
  RUN_TEST(test_vertical_rate_zero_has_no_sign);
  RUN_TEST(test_status_ground);
  RUN_TEST(test_status_climb_and_descent_thresholds);
  RUN_TEST(test_status_stale_overrides_motion);
  RUN_TEST(test_status_emergency_overrides_stale_and_motion);
  RUN_TEST(test_battery_unknown_when_invalid);
  RUN_TEST(test_battery_100_percent_text_fits);
  RUN_TEST(test_battery_color_tiers);
  RUN_TEST(test_data_age_live_within_one_poll_interval);
  RUN_TEST(test_data_age_shows_seconds_while_aging);
  RUN_TEST(test_data_age_stale_flag_wins_regardless_of_age);
  return UNITY_END();
}
