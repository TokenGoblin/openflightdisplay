#include <unity.h>

#include <cstring>

#include "domain/protocol.h"

using namespace ofd;

void setUp() {}
void tearDown() {}

void test_parses_heartbeat() {
  const char* json = R"({"schemaVersion":1,"type":"heartbeat","serverTime":"2026-07-24T12:00:00.000Z"})";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
  TEST_ASSERT_TRUE(msg.type == ServerMessageType::Heartbeat);
}

void test_parses_provider_status() {
  const char* json =
      R"({"schemaVersion":1,"type":"provider-status","provider":"adsblol",)"
      R"("status":"unavailable","message":"adsb.lol unreachable, retrying"})";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
  TEST_ASSERT_TRUE(msg.type == ServerMessageType::ProviderStatus);
  TEST_ASSERT_EQUAL_STRING("adsblol", msg.providerId);
  TEST_ASSERT_TRUE(msg.providerHealth == ProviderHealth::Unavailable);
}

void test_parses_aircraft_update() {
  const char* json =
      R"({"schemaVersion":1,"type":"aircraft-update","generatedAt":"2026-07-24T12:00:00.000Z",)"
      R"("aircraft":[{"provider":"mock","icaoHex":"a1b2c3","callsign":"UAL123",)"
      R"("latitude":47.61,"longitude":-122.3,"geometricAltitudeFt":8500,"groundSpeedKt":240,)"
      R"("onGround":false,"emergencyState":"none","distanceFromObserverKm":1.2,)"
      R"("bearingFromObserverDeg":90,"positionTimestamp":"2026-07-24T12:00:00.000Z"}]})";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_TRUE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
  TEST_ASSERT_TRUE(msg.type == ServerMessageType::AircraftUpdate);
  TEST_ASSERT_EQUAL_UINT(1, msg.aircraft.count);
  TEST_ASSERT_EQUAL_STRING("a1b2c3", msg.aircraft.items[0].icaoHex);
  TEST_ASSERT_TRUE(msg.aircraft.items[0].hasAltitudeFt);
  TEST_ASSERT_EQUAL_DOUBLE(8500.0, msg.aircraft.items[0].altitudeFt);
}

void test_rejects_unsupported_schema_version() {
  const char* json = R"({"schemaVersion":999,"type":"heartbeat","serverTime":"2026-07-24T12:00:00.000Z"})";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
}

void test_rejects_unknown_message_type() {
  const char* json = R"({"schemaVersion":1,"type":"something-else"})";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
}

void test_rejects_truncated_json_without_crashing() {
  const char* json = R"({"schemaVersion":1,"type":"aircraft-update","aircraft":[{"icaoHex":"a1b2c)";
  ParsedServerMessage msg;
  char error[64] = {0};
  TEST_ASSERT_FALSE(parseServerMessage(json, std::strlen(json), msg, error, sizeof(error)));
}

void test_build_hello_message() {
  char buf[192];
  const size_t written = buildHelloMessage("core2-abc123", "core2", buf, sizeof(buf));
  TEST_ASSERT_TRUE(written > 0);
  TEST_ASSERT_TRUE(std::strstr(buf, "\"type\":\"hello\"") != nullptr);
  TEST_ASSERT_TRUE(std::strstr(buf, "core2-abc123") != nullptr);
  TEST_ASSERT_TRUE(std::strstr(buf, "\"role\":\"core2\"") != nullptr);
}

// The role travels with the board rather than being baked in, so a
// second board kind identifies itself as itself.
void test_build_hello_message_carries_board_role() {
  char buf[192];
  const size_t written = buildHelloMessage("tab5-1c40e2", "tab5", buf, sizeof(buf));
  TEST_ASSERT_TRUE(written > 0);
  TEST_ASSERT_TRUE(std::strstr(buf, "tab5-1c40e2") != nullptr);
  TEST_ASSERT_TRUE(std::strstr(buf, "\"role\":\"tab5\"") != nullptr);
  TEST_ASSERT_TRUE(std::strstr(buf, "core2") == nullptr);
}

int main(int argc, char** argv) {
  UNITY_BEGIN();
  RUN_TEST(test_parses_heartbeat);
  RUN_TEST(test_parses_provider_status);
  RUN_TEST(test_parses_aircraft_update);
  RUN_TEST(test_rejects_unsupported_schema_version);
  RUN_TEST(test_rejects_unknown_message_type);
  RUN_TEST(test_rejects_truncated_json_without_crashing);
  RUN_TEST(test_build_hello_message);
  RUN_TEST(test_build_hello_message_carries_board_role);
  return UNITY_END();
}
