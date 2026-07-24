#pragma once

#include "app/config_store.h"
#include "app/device_identity.h"
#include "domain/config.h"
#include "domain/protocol.h"

namespace ofd::app {

enum class WifiState { Disconnected, Provisioning, Connected };
enum class GatewayConnectionState { Unconfigured, Connecting, Connected, Disconnected };

// Central, in-RAM view of device state, shared by reference across the
// app-layer modules (pairing server, gateway client, display, main
// loop) so none of them need to independently reload from LittleFS on
// every access. ConfigStore remains the source of truth on disk; this
// is a cache of it plus transient runtime state.
struct AppContext {
  char deviceId[20] = {0};
  const char* firmwareVersion = "0.1.0";

  ConfigStore configStore;
  DeviceConfig config;
  bool hasConfig = false;

  char pairingToken[40] = {0};
  bool hasPairingToken = false;

  PairingCodeManager pairingCodeManager;

  WifiState wifiState = WifiState::Disconnected;
  GatewayConnectionState gatewayState = GatewayConnectionState::Unconfigured;

  AircraftList latestAircraft;
  bool hasLatestAircraft = false;
  uint32_t lastAircraftUpdateAtMs = 0;
  uint32_t lastServerMessageAtMs = 0;

  ProviderHealth providerHealth = ProviderHealth::Ok;
  char providerStatusMessage[128] = {0};
};

}  // namespace ofd::app
