#pragma once

#include "domain/config.h"

namespace ofd::app {

// LittleFS-backed persistence for DeviceConfig and the pairing token.
// Kept as two separate files so a config update can never corrupt or
// touch the pairing token and vice versa. Every write is atomic
// (write to a `.tmp` path, then LittleFS.rename over the real path) so
// a power loss mid-write can never leave a corrupt file behind -- see
// docs/ARCHITECTURE.md's atomic-configuration-write requirement.
//
// Callers must have already called LittleFS.begin() (done once in
// main.cpp's setup()).
class ConfigStore {
 public:
  // Returns false if no config has ever been saved, or if the stored
  // file is corrupt/invalid -- in both cases the caller should treat
  // this as "configuration required" rather than crash or guess.
  bool loadConfig(DeviceConfig& out);
  bool saveConfig(const DeviceConfig& config);

  bool loadPairingToken(char* outBuf, size_t outBufLen);
  bool savePairingToken(const char* token);

  static constexpr const char* kConfigPath = "/config.json";
  static constexpr const char* kPairingPath = "/pairing.json";
};

}  // namespace ofd::app
