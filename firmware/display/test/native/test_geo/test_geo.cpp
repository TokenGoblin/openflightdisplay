#include <unity.h>

#include <cmath>

#include "domain/geo.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

void test_distance_to_self_is_zero() {
  TEST_ASSERT_DOUBLE_WITHIN(0.001, 0.0, haversineDistanceKm(47.6, -122.3, 47.6, -122.3));
}

void test_distance_known_pair() {
  // Seattle (47.6062, -122.3321) to Portland (45.5152, -122.6784):
  // ~233 km great-circle distance.
  const double d = haversineDistanceKm(47.6062, -122.3321, 45.5152, -122.6784);
  TEST_ASSERT_DOUBLE_WITHIN(5.0, 233.0, d);
}

void test_bearing_due_north_is_zero() {
  const double bearing = initialBearingDeg(0.0, 0.0, 1.0, 0.0);
  TEST_ASSERT_DOUBLE_WITHIN(0.5, 0.0, bearing);
}

void test_bearing_due_east_is_90() {
  const double bearing = initialBearingDeg(0.0, 0.0, 0.0, 1.0);
  TEST_ASSERT_DOUBLE_WITHIN(0.5, 90.0, bearing);
}

void test_bearing_is_always_in_range_0_360() {
  const double bearing = initialBearingDeg(10.0, 10.0, -10.0, -170.0);
  TEST_ASSERT_TRUE(bearing >= 0.0 && bearing < 360.0);
}

void test_is_within_circle_boundary_cases() {
  // Exactly-at-radius should count as inside (<=), per the shared spec.
  const double centerLat = 47.6, centerLon = -122.3, radiusKm = 10.0;
  // Move ~10km north (1 degree lat ~= 111.32km).
  const double atEdgeLat = centerLat + (radiusKm / 111.32);
  TEST_ASSERT_TRUE(isWithinCircle(atEdgeLat, centerLon, centerLat, centerLon, radiusKm + 0.01));
  TEST_ASSERT_FALSE(isWithinCircle(centerLat + 5.0, centerLon, centerLat, centerLon, radiusKm));
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_distance_to_self_is_zero);
  RUN_TEST(test_distance_known_pair);
  RUN_TEST(test_bearing_due_north_is_zero);
  RUN_TEST(test_bearing_due_east_is_90);
  RUN_TEST(test_bearing_is_always_in_range_0_360);
  RUN_TEST(test_is_within_circle_boundary_cases);
  return UNITY_END();
}
