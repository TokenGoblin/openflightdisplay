#include <unity.h>

#include <cstdio>
#include <cstring>

#include "domain/ranking.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

namespace {
AircraftState makeAircraft(const char* icaoHex, double lat, double lon) {
  AircraftState a;
  std::strncpy(a.icaoHex, icaoHex, sizeof(a.icaoHex) - 1);
  a.latitude = lat;
  a.longitude = lon;
  return a;
}
}  // namespace

void test_orders_by_distance_nearest_first() {
  CircleMonitoringArea area;
  area.centerLat = 47.6;
  area.centerLon = -122.3;
  area.radiusKm = 10.0;

  AircraftList input;
  input.items[0] = makeAircraft("aaaaaa", 47.65, -122.35);   // farther
  input.items[1] = makeAircraft("bbbbbb", 47.601, -122.301);  // nearer
  input.count = 2;

  const AircraftList result = rankNearest(input, area);
  TEST_ASSERT_EQUAL_UINT(2, result.count);
  TEST_ASSERT_EQUAL_STRING("bbbbbb", result.items[0].icaoHex);
  TEST_ASSERT_EQUAL_STRING("aaaaaa", result.items[1].icaoHex);
}

void test_excludes_aircraft_outside_radius() {
  CircleMonitoringArea area;
  area.centerLat = 47.6;
  area.centerLon = -122.3;
  area.radiusKm = 10.0;

  AircraftList input;
  input.items[0] = makeAircraft("cccccc", 48.5, -123.5);  // far outside
  input.count = 1;

  const AircraftList result = rankNearest(input, area);
  TEST_ASSERT_EQUAL_UINT(0, result.count);
}

void test_fills_in_distance_and_bearing() {
  CircleMonitoringArea area;
  area.centerLat = 47.6;
  area.centerLon = -122.3;
  area.radiusKm = 10.0;

  AircraftList input;
  input.items[0] = makeAircraft("bbbbbb", 47.61, -122.3);
  input.count = 1;

  const AircraftList result = rankNearest(input, area);
  TEST_ASSERT_EQUAL_UINT(1, result.count);
  TEST_ASSERT_TRUE(result.items[0].hasDistanceFromObserverKm);
  TEST_ASSERT_TRUE(result.items[0].hasBearingFromObserverDeg);
  TEST_ASSERT_TRUE(result.items[0].distanceFromObserverKm > 0.0);
}

void test_caps_results_at_max() {
  CircleMonitoringArea area;
  area.centerLat = 47.6;
  area.centerLon = -122.3;
  area.radiusKm = 50.0;

  AircraftList input;
  for (size_t i = 0; i < 5; i++) {
    char hex[7];
    std::snprintf(hex, sizeof(hex), "%06zu", i);
    input.items[i] = makeAircraft(hex, 47.601, -122.301);
  }
  input.count = 5;

  const AircraftList result = rankNearest(input, area, 2);
  TEST_ASSERT_EQUAL_UINT(2, result.count);
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_orders_by_distance_nearest_first);
  RUN_TEST(test_excludes_aircraft_outside_radius);
  RUN_TEST(test_fills_in_distance_and_bearing);
  RUN_TEST(test_caps_results_at_max);
  return UNITY_END();
}
