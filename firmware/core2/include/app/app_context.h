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
//
// Threading note: this struct is written from three different FreeRTOS
// tasks with no mutex -- loopTask (main.cpp's setup/loop), the async web
// server's task (pairing_server.cpp's route handlers), and, since
// GatewayClient::begin() spawns its own task for stack-size reasons (see
// gateway_client.h), a dedicated WS task too. No lock is taken
// deliberately: every field here is either a POD type (bool/enum/word-
// sized int, atomic in practice on this architecture) or a fixed-size
// struct copied wholesale (AircraftList/DeviceConfig) where a rare
// torn read just means one render frame shows slightly stale data, not
// a crash. This mirrors a pattern already present before the WS task
// existed (the async server task was already writing hasConfig/config
// without a lock). Revisit with a real mutex if a future change makes
// these updates more frequent or more consequential to get wrong.
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
