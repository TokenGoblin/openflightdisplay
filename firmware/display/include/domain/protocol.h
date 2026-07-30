#pragma once

#include <cstddef>
#include <cstdint>

#include "domain/aircraft.h"

namespace ofd {

// Hand-maintained mirror of packages/protocol/src/wsMessages.ts. Keep the
// two in sync -- docs/PROTOCOL.md is the contract of record if they ever
// disagree. Only schemaVersion 1 is understood; anything else is rejected
// rather than best-effort parsed (see docs/PROTOCOL.md's versioning policy).
constexpr int kCurrentSchemaVersion = 1;

enum class ServerMessageType {
  Unknown,
  AircraftUpdate,
  Heartbeat,
  ProviderStatus,
};

enum class ProviderHealth {
  Ok,
  Degraded,
  Unavailable,
};

struct ParsedServerMessage {
  ServerMessageType type = ServerMessageType::Unknown;

  // Populated when type == AircraftUpdate.
  AircraftList aircraft;

  // Populated when type == ProviderStatus.
  char providerId[16] = {0};
  ProviderHealth providerHealth = ProviderHealth::Ok;
  char statusMessage[128] = {0};
};

// Parses one WS text frame from the gateway. Returns false (message.type
// left as Unknown) for: a schemaVersion other than kCurrentSchemaVersion,
// an unrecognized "type", malformed JSON, or a payload that doesn't fit
// the bounded parsing buffer -- all of which must be treated as
// "ignore this frame, don't crash, don't misrender."
bool parseServerMessage(const char* json, size_t len, ParsedServerMessage& out, char* errorOut,
                         size_t errorOutLen);

// Builds the client->server "hello" message sent once after connecting.
// Returns the number of bytes written (excluding NUL), or 0 if `buf` was
// too small.
//
// `role` identifies the kind of client and must be one of the values in
// docs/PROTOCOL.md's role enum ("core2", "tab5", "pwa"). It's a
// parameter rather than a constant because this file is domain/ code
// compiled for the hardware-free native test build, where the board
// layer doesn't exist -- the caller, which does know its board, passes
// ofd::board::kDeviceIdPrefix.
size_t buildHelloMessage(const char* deviceId, const char* role, char* buf, size_t bufLen);

}  // namespace ofd
