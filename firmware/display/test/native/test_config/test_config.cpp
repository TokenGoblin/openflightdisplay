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

// ---- tracked flight ----

void test_accepts_tracked_flight_and_normalizes_the_callsign() {
  const char* json =
      "{\"deviceId\":\"core2-abc123\",\"trackedFlight\":{\"flight\":\"UA1234\",\"destinationIcao\":\"KSEA\"}}";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_TRUE(config.hasTrackedFlight);
  // Queried against ADS-B as the ICAO callsign...
  TEST_ASSERT_EQUAL_STRING("UAL1234", config.trackedFlight.callsign);
  // ...but shown back to the user as the flight number they typed.
  TEST_ASSERT_EQUAL_STRING("UA1234", config.trackedFlight.label);
  TEST_ASSERT_EQUAL_STRING("KSEA", config.trackedFlight.destinationIcao);
}

void test_uppercases_destination_icao() {
  const char* json =
      "{\"deviceId\":\"core2-abc123\",\"trackedFlight\":{\"flight\":\"BA249\",\"destinationIcao\":\"egll\"}}";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_EQUAL_STRING("EGLL", config.trackedFlight.destinationIcao);
}

// IATA airport codes are rejected rather than guessed at -- the airport
// endpoint answers null for them, and "SEA" -> "KSEA" is a North
// America-only assumption.
void test_rejects_iata_destination_code() {
  const char* json =
      "{\"deviceId\":\"core2-abc123\",\"trackedFlight\":{\"flight\":\"UA1234\",\"destinationIcao\":\"SEA\"}}";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_TRUE(std::strlen(error) > 0);
}

void test_rejects_tracked_flight_without_a_flight_number() {
  const char* json =
      "{\"deviceId\":\"core2-abc123\",\"trackedFlight\":{\"flight\":\"UNITED\",\"destinationIcao\":\"KSEA\"}}";
  DeviceConfig config;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
}

// Explicit null is how the setup UI cancels an airport run.
void test_null_tracked_flight_clears_tracking() {
  DeviceConfig config;
  std::strcpy(config.deviceId, "core2-abc123");
  config.hasTrackedFlight = true;
  std::strcpy(config.trackedFlight.callsign, "UAL1234");

  const char* json = "{\"deviceId\":\"core2-abc123\",\"trackedFlight\":null}";
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_FALSE(config.hasTrackedFlight);
}

// Absent is not the same as null: a PUT that only changes brightness
// must not silently cancel the flight somebody is waiting on.
void test_absent_tracked_flight_is_preserved_across_partial_update() {
  DeviceConfig config;
  std::strcpy(config.deviceId, "core2-abc123");
  config.hasTrackedFlight = true;
  std::strcpy(config.trackedFlight.callsign, "UAL1234");
  std::strcpy(config.trackedFlight.label, "UA1234");
  std::strcpy(config.trackedFlight.destinationIcao, "KSEA");

  const char* json = "{\"deviceId\":\"core2-abc123\",\"displayProfile\":{\"brightness\":120}}";
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(json, std::strlen(json), config, error, sizeof(error)));
  TEST_ASSERT_TRUE(config.hasTrackedFlight);
  TEST_ASSERT_EQUAL_STRING("UAL1234", config.trackedFlight.callsign);
  TEST_ASSERT_EQUAL_UINT8(120, config.brightness);
}

void test_tracked_flight_survives_serialize_round_trip() {
  DeviceConfig original;
  std::strcpy(original.deviceId, "tab5-1c40e2");
  original.hasTrackedFlight = true;
  std::strcpy(original.trackedFlight.callsign, "BAW249");
  std::strcpy(original.trackedFlight.label, "BA249");
  std::strcpy(original.trackedFlight.destinationIcao, "KSEA");

  char buf[768];
  const size_t written = serializeDeviceConfig(original, buf, sizeof(buf));
  TEST_ASSERT_TRUE(written > 0);

  DeviceConfig reparsed;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseAndValidateDeviceConfig(buf, written, reparsed, error, sizeof(error)));
  TEST_ASSERT_TRUE(reparsed.hasTrackedFlight);
  TEST_ASSERT_EQUAL_STRING("BAW249", reparsed.trackedFlight.callsign);
  TEST_ASSERT_EQUAL_STRING("BA249", reparsed.trackedFlight.label);
  TEST_ASSERT_EQUAL_STRING("KSEA", reparsed.trackedFlight.destinationIcao);
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
  RUN_TEST(test_accepts_tracked_flight_and_normalizes_the_callsign);
  RUN_TEST(test_uppercases_destination_icao);
  RUN_TEST(test_rejects_iata_destination_code);
  RUN_TEST(test_rejects_tracked_flight_without_a_flight_number);
  RUN_TEST(test_null_tracked_flight_clears_tracking);
  RUN_TEST(test_absent_tracked_flight_is_preserved_across_partial_update);
  RUN_TEST(test_tracked_flight_survives_serialize_round_trip);
  return UNITY_END();
}
