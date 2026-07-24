#include <unity.h>

#include "domain/staleness.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

void test_flags_old_position_as_stale() {
  const int64_t now = 1000000;
  const int64_t old = now - kStalePositionThresholdMs - 1;
  TEST_ASSERT_TRUE(isStalePosition(old, now));
}

void test_does_not_flag_fresh_position() {
  const int64_t now = 1000000;
  const int64_t fresh = now - 1000;
  TEST_ASSERT_FALSE(isStalePosition(fresh, now));
}

void test_boundary_exactly_at_threshold_is_not_stale() {
  const int64_t now = 1000000;
  const int64_t atThreshold = now - kStalePositionThresholdMs;
  TEST_ASSERT_FALSE(isStalePosition(atThreshold, now));
}

void test_connection_dead_detection() {
  const int64_t now = 1000000;
  TEST_ASSERT_TRUE(isConnectionDead(now - kDeadConnectionTimeoutMs - 1, now));
  TEST_ASSERT_FALSE(isConnectionDead(now - 1000, now));
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_flags_old_position_as_stale);
  RUN_TEST(test_does_not_flag_fresh_position);
  RUN_TEST(test_boundary_exactly_at_threshold_is_not_stale);
  RUN_TEST(test_connection_dead_detection);
  return UNITY_END();
}
