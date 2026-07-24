#pragma once

#include <ESPAsyncWebServer.h>

#include <cstddef>
#include <cstdint>

namespace ofd::app {

struct WifiCredentials {
  char ssid[33] = {0};
  char password[65] = {0};
};

// Loads previously-saved Wi-Fi credentials from LittleFS. Returns false
// if none have been saved yet (first boot, or after a factory reset).
bool loadWifiCredentials(WifiCredentials& out);

// Atomically persists Wi-Fi credentials (write-temp-then-rename, same
// pattern as ConfigStore).
bool saveWifiCredentials(const WifiCredentials& creds);

// Starts the SoftAP + captive portal used for first-time setup
// (docs/PROVISIONING.md). `apName` should already include the device's
// short id suffix. Registers its own route handlers on the shared
// AsyncWebServer instance passed in from main.cpp.
void startProvisioningAccessPoint(AsyncWebServer& server, const char* apName);

// Must be called every loop() iteration while the AP is active, so the
// captive-portal DNS redirect keeps working.
void processProvisioningDns();

// Attempts to join `creds` in station mode, blocking up to `timeoutMs`.
// Returns true on success. On repeated failure, callers should fall back
// to startProvisioningAccessPoint() again rather than retrying forever
// (docs/PROVISIONING.md's "wrong Wi-Fi password" failure handling).
bool connectToWifi(const WifiCredentials& creds, uint32_t timeoutMs);

}  // namespace ofd::app
