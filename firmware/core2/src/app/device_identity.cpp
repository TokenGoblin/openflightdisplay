#include "app/device_identity.h"

#include <Arduino.h>
#include <esp_system.h>

#include <cstdio>
#include <cstring>

namespace ofd::app {

bool getDeviceId(char* outBuf, size_t outBufLen) {
  if (outBufLen < 20) return false;
  const uint64_t mac = ESP.getEfuseMac();
  // Use the low 24 bits -- plenty of entropy for a LAN-local id, and
  // keeps the id short enough to be comfortably typed by hand as a
  // manual-entry fallback (docs/PROVISIONING.md).
  const uint32_t shortId = static_cast<uint32_t>(mac & 0xFFFFFF);
  std::snprintf(outBuf, outBufLen, "core2-%06x", shortId);
  return true;
}

void PairingCodeManager::regenerate(uint32_t nowMs) {
  const uint32_t value = esp_random() % 1000000u;
  std::snprintf(code_, sizeof(code_), "%06u", value);
  issuedAtMs_ = nowMs;
  claimed_ = false;
}

bool PairingCodeManager::tryClaim(const char* code, uint32_t nowMs) {
  if (claimed_) return false;
  if ((nowMs - issuedAtMs_) > kExpiryMs) return false;
  if (std::strcmp(code, code_) != 0) return false;
  claimed_ = true;
  return true;
}

}  // namespace ofd::app
