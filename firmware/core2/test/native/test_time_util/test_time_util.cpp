#include <unity.h>

#include "domain/time_util.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

void test_epoch_zero() {
  int64_t ms = -1;
  TEST_ASSERT_TRUE(parseIso8601ToEpochMs("1970-01-01T00:00:00Z", ms));
  TEST_ASSERT_EQUAL_INT64(0, ms);
}

void test_with_milliseconds() {
  int64_t ms = -1;
  TEST_ASSERT_TRUE(parseIso8601ToEpochMs("1970-01-01T00:00:00.500Z", ms));
  TEST_ASSERT_EQUAL_INT64(500, ms);
}

void test_known_recent_timestamp() {
  // 2026-07-24T12:00:00.000Z
  int64_t ms = -1;
  TEST_ASSERT_TRUE(parseIso8601ToEpochMs("2026-07-24T12:00:00.000Z", ms));
  // Sanity bound rather than a hand-computed exact value: must be a
  // plausible number of milliseconds since epoch for the year 2026.
  TEST_ASSERT_TRUE(ms > 1770000000000LL && ms < 1800000000000LL);
}

void test_rejects_malformed_input() {
  int64_t ms = -1;
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs("not-a-timestamp", ms));
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs("", ms));
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs(nullptr, ms));
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs("2026-13-01T00:00:00Z", ms));  // invalid month
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs("2026-02-30T00:00:00Z", ms));  // invalid day (not a leap day either)
  TEST_ASSERT_FALSE(parseIso8601ToEpochMs("2026-07-24T12:00:00", ms));   // missing Z
}

void test_leap_year_day_is_valid() {
  int64_t ms = -1;
  TEST_ASSERT_TRUE(parseIso8601ToEpochMs("2024-02-29T00:00:00Z", ms));
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_epoch_zero);
  RUN_TEST(test_with_milliseconds);
  RUN_TEST(test_known_recent_timestamp);
  RUN_TEST(test_rejects_malformed_input);
  RUN_TEST(test_leap_year_day_is_valid);
  return UNITY_END();
}
