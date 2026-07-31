#pragma once

#include "app/config_store.h"
#include "app/device_identity.h"
#include "app/page.h"
#include "domain/battery.h"
#include "domain/config.h"
#include "domain/flight_tracking.h"
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
  // "<board-prefix>-8a2f19" -- see ofd::board::kDeviceIdPrefix and
  // getDeviceId().
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

  // ---- tracked flight (see domain/flight_tracking.h) ----
  //
  // Written by the poll task, read by the renderer, same
  // no-mutex-by-design rule as everything else in this struct.
  //
  // `trackedEverSeen` is the field that distinguishes "hasn't departed
  // yet" from "we lost it", which no single position report can express
  // and which the two states are rendered very differently from.
  ofd::Airport trackedDestination;
  ofd::AircraftState trackedAircraft;
  bool trackedEverSeen = false;
  uint32_t trackedLastSeenAtMs = 0;
  ofd::FlightProgress trackedProgress;
  // Set when the destination ICAO didn't resolve to an airport. A
  // typo'd airport is otherwise indistinguishable from a flight that
  // hasn't taken off, and only one of those is the user's mistake.
  bool trackedDestinationUnresolved = false;

  // True once the configured flight is being followed and hasn't landed.
  // The primary page shows the tracked flight while this holds.
  bool trackingActive() const {
    return hasConfig && config.hasTrackedFlight &&
           trackedProgress.phase != ofd::FlightPhase::Landed;
  }

  ofd::BatteryState battery;
};

}  // namespace ofd::app
