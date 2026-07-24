#pragma once

#include <cstdint>

namespace ofd::app {

// Derives a stable "core2-xxxxxx" id from the ESP32's factory-programmed
// MAC (via esp_efuse_mac_get_default), so it survives reflashing and
// factory reset without needing to persist anything. Writes into
// `outBuf` (must be at least 20 bytes) and returns false if the buffer
// is too small.
bool getDeviceId(char* outBuf, size_t outBufLen);

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
