#pragma once

#include <cstddef>
#include <cstdint>

namespace ofd::app {

// Derives a stable "<board>-xxxxxx" id (e.g. "core2-8a2f19",
// "tab5-1c40e2" -- the prefix comes from ofd::board::kDeviceIdPrefix)
// from the chip's factory-programmed MAC, so it survives reflashing and
// factory reset without needing to persist anything. Writes into
// `outBuf` (must be at least 20 bytes) and returns false if the buffer
// is too small.
//
// The id is user-visible and load-bearing: it's the mDNS hostname, the
// OTA upload target, and the "DEVICE ID" row on the SYSTEM page. Two
// boards of different kinds on one network can't collide even in the
// (astronomically unlikely) event their MAC low bytes match.
bool getDeviceId(char* outBuf, size_t outBufLen);

// The portion of the device id after the "<board>-" prefix -- the short
// hex suffix on its own, for places that want a compact unique tag
// rather than the full id (the setup access point's SSID, for one).
// Returns a pointer into `deviceId`, or the whole string if it somehow
// has no prefix.
const char* deviceIdSuffix(const char* deviceId);

// Manages the single-use, time-limited pairing code shown on-screen
// during setup (see docs/PROVISIONING.md). Not thread-safe -- only
// touched from the main loop.
class PairingCodeManager {
 public:
  // Generates a fresh 6-digit code, valid for kExpiryMs from now.
  void regenerate(uint32_t nowMs);

  // Consumes the code if it matches and hasn't expired. A code can only
  // ever be claimed once (claiming invalidates it immediately) --
  // matches docs/PROTOCOL.md's "single-use" pairing code requirement.
  bool tryClaim(const char* code, uint32_t nowMs);

  const char* currentCode() const { return code_; }

  static constexpr uint32_t kExpiryMs = 10 * 60 * 1000;

 private:
  char code_[7] = "000000";
  uint32_t issuedAtMs_ = 0;
  bool claimed_ = false;
};

}  // namespace ofd::app
