#pragma once

#include "app/config_store.h"
#include "app/device_identity.h"
#include "domain/battery.h"
#include "domain/config.h"
#include "domain/protocol.h"

namespace ofd::app {

enum class WifiState { Disconnected, Provisioning, Connected };

// Which of the 3 bottom-button pages is currently selected -- BtnA/BtnB/
// BtnC in main.cpp's loop() cycle this directly (not prev/next) so each
// physical button always means the same page. See docs/CORE2_DISPLAY.md
// "Page navigation".
enum class DetailPage : uint8_t { Flight, Detail, System };

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
  // Live WiFi.status() reading, refreshed periodically by main.cpp's loop()
  // while wifiState == Connected. Distinct from wifiState, which only
  // tracks the boot-time provisioning/connect phase and is never
  // downgraded again once connected -- this field is what the header's
  // Wi-Fi icon and the WIFI OFFLINE screen actually key off of.
  bool wifiConnected = false;

  DetailPage currentPage = DetailPage::Flight;

  AircraftList latestAircraft;
  bool hasLatestAircraft = false;
  uint32_t lastAircraftUpdateAtMs = 0;

  ProviderHealth providerHealth = ProviderHealth::Ok;
  bool providerStarted = false;

  ofd::BatteryState battery;
};

}  // namespace ofd::app
