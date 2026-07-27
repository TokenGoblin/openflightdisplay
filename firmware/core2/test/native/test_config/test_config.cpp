#include <unity.h>

#include <cstring>

#include "domain/config.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

void test_accepts_valid_minimal_config() {
  const char* json = R"({"deviceId":"core2-abc123","deviceName":"Living Room"})";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_EQUAL_STRING("core2-abc123", config.deviceId);
  TEST_ASSERT_EQUAL_STRING("Living Room", config.deviceName);
  TEST_ASSERT_FALSE(config.hasMonitoringArea);
}

void test_accepts_full_config_with_circle_area() {
  const char* json =
      R"({"deviceId":"core2-abc123","deviceName":"Living Room",)"
      R"("monitoringArea":{"kind":"circle","centerLat":47.6,"centerLon":-122.3,"radiusKm":15},)"
      R"("displayProfile":{"brightness":180}})";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_TRUE(config.hasMonitoringArea);
  TEST_ASSERT_EQUAL_DOUBLE(15.0, config.monitoringArea.radiusKm);
  TEST_ASSERT_EQUAL_UINT8(180, config.brightness);
}

void test_rejects_malformed_json_without_crashing() {
  const char* json = "{ this is not valid json";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_TRUE(std::strlen(error) > 0);
}

void test_rejects_missing_device_id() {
  const char* json = R"({"deviceName":"Living Room"})";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
}

void test_partial_update_keeps_existing_device_id() {
  const char* json = R"({"displayProfile":{"brightness":150}})";
  DeviceConfig config;
  std::strcpy(config.deviceId, "core2-existing");
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_EQUAL_STRING("core2-existing", config.deviceId);
  TEST_ASSERT_EQUAL_UINT8(150, config.brightness);
}

void test_rejects_unsupported_monitoring_area_kind() {
  const char* json =
      R"({"deviceId":"core2-abc123","deviceName":"Living Room",)"
      R"("monitoringArea":{"kind":"polygon","vertices":[]}})";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
}

void test_rejects_out_of_range_radius() {
  const char* json =
      R"({"deviceId":"core2-abc123","deviceName":"Living Room",)"
      R"("monitoringArea":{"kind":"circle","centerLat":47.6,"centerLon":-122.3,"radiusKm":5000}})";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
}

void test_serialize_round_trip() {
  DeviceConfig original;
  std::strcpy(original.deviceId, "core2-abc123");
  std::strcpy(original.deviceName, "Living Room");
  original.hasMonitoringArea = true;
  original.monitoringArea.centerLat = 47.6;
  original.monitoringArea.centerLon = -122.3;
  original.monitoringArea.radiusKm = 15.0;
  original.brightness = 180;

  char buf[512];
  const size_t written = serializeDeviceConfig(original, buf, sizeof(buf));
  TEST_ASSERT_TRUE(written > 0);

  DeviceConfig reparsed;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(buf, written, reparsed, error, sizeof(error)));
  TEST_ASSERT_EQUAL_STRING(original.deviceId, reparsed.deviceId);
  TEST_ASSERT_EQUAL_DOUBLE(original.monitoringArea.radiusKm, reparsed.monitoringArea.radiusKm);
  TEST_ASSERT_EQUAL_UINT8(original.brightness, reparsed.brightness);
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_accepts_valid_minimal_config);
  RUN_TEST(test_accepts_full_config_with_circle_area);
  RUN_TEST(test_rejects_malformed_json_without_crashing);
  RUN_TEST(test_rejects_missing_device_id);
  RUN_TEST(test_partial_update_keeps_existing_device_id);
  RUN_TEST(test_rejects_unsupported_monitoring_area_kind);
  RUN_TEST(test_rejects_out_of_range_radius);
  RUN_TEST(test_serialize_round_trip);
  return UNITY_END();
}
