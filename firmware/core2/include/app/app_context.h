#pragma once

#include "app/config_store.h"
#include "app/device_identity.h"
#include "domain/config.h"
#include "domain/protocol.h"

namespace ofd::app {

enum class WifiState { Disconnected, Provisioning, Connected };

// Central, in-RAM view of device state, shared by reference across the
// app-layer modules (pairing server, adsb provider, display, main
// loop) so none of them need to independently reload from LittleFS on
// every access. ConfigStore remains the source of truth on disk; this
// is a cache of it plus transient runtime state.
//
// Threading note: this struct is written from multiple FreeRTOS tasks
// with no mutex -- loopTask, the async web server's task, and the
// adsb poller task. No lock is taken deliberately: every field here is
// either a POD type (bool/enum/word-sized int, atomic in practice on
// this architecture) or a fixed-size struct copied wholesale where a
// rare torn read just means one render frame shows slightly stale data,
// not a crash. Revisit with a real mutex if a future change makes these
// updates more frequent or more consequential to get wrong.
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

  AircraftList latestAircraft;
  bool hasLatestAircraft = false;
  uint32_t lastAircraftUpdateAtMs = 0;

  ProviderHealth providerHealth = ProviderHealth::Ok;
  bool providerStarted = false;
};

}  // namespace ofd::app
