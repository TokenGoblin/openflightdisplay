#include <ESPAsyncWebServer.h>
#include <ESPmDNS.h>
#include <LittleFS.h>
#include <M5Unified.h>
#include <WiFi.h>
#include <time.h>

#include <cstring>

#include "app/adsb_provider.h"
#include "app/app_context.h"
#include "app/config_store.h"
#include "app/device_identity.h"
#include "app/display.h"
#include "app/pairing_server.h"
#include "app/wifi_provisioning.h"
#include "domain/staleness.h"

using namespace ofd;
using namespace ofd::app;

namespace {

AsyncWebServer g_server(80);
AppContext g_ctx;
Display g_display;
AdsbProvider g_adsbProvider;

bool g_wifiJustSaved = false;
uint32_t g_wifiJustSavedAtMs = 0;
uint32_t g_lastPairingCodeRegenAtMs = 0;
uint32_t g_lastWifiCredsCheckAtMs = 0;

constexpr uint32_t kWifiConnectTimeoutMs = 15000;
constexpr uint32_t kWifiSaveRebootDelayMs = 1500;
constexpr uint32_t kWifiCredsCheckIntervalMs = 1000;
// Redraw interval. Aircraft data arrives every ~15 s from adsb.lol;
// the clock ticks every minute. A 5 s interval eliminates the visible
// flicker-blink of repeated full-screen fills while keeping the age
// indicator acceptably fresh.
constexpr uint32_t kRenderIntervalMs = 5000;

uint32_t g_lastRenderAtMs = 0;

bool ntpTimeIsPlausible() {
  return time(nullptr) > 1700000000;
}

void enterProvisioningMode() {
  char apName[32];
  std::snprintf(apName, sizeof(apName), "OpenFlightDisplay-Setup-%s", g_ctx.deviceId + 6);
  startProvisioningAccessPoint(g_server, apName);
  g_ctx.wifiState = WifiState::Provisioning;
  g_display.renderProvisioning(apName);
}

void startDataSource() {
  if (g_ctx.hasConfig && g_ctx.config.hasMonitoringArea) {
    g_adsbProvider.begin(g_ctx);
    g_ctx.providerStarted = true;
  }
}

void enterConnectedMode() {
  g_ctx.wifiState = WifiState::Connected;
  configTime(0, 0, "pool.ntp.org", "time.nist.gov");
  MDNS.begin(g_ctx.deviceId);
  MDNS.addService("openflightdisplay", "tcp", 80);

  registerPairingRoutes(g_server, g_ctx);
  g_server.begin();

  if (!g_ctx.hasConfig || !g_ctx.config.hasMonitoringArea) {
    g_ctx.pairingCodeManager.regenerate(millis());
    g_lastPairingCodeRegenAtMs = millis();
    g_display.renderPairingReady(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
  } else {
    startDataSource();
  }
}

void renderCurrentState() {
  if (g_ctx.wifiState == WifiState::Provisioning) return;

  if (!g_ctx.hasConfig || !g_ctx.config.hasMonitoringArea) {
    if (millis() - g_lastPairingCodeRegenAtMs > PairingCodeManager::kExpiryMs) {
      g_ctx.pairingCodeManager.regenerate(millis());
      g_lastPairingCodeRegenAtMs = millis();
      g_display.renderPairingReady(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
    }
    return;
  }

  // Start the data source if it wasn't started at boot (config arrived
  // later via the setup page while the device was already online).
  if (!g_ctx.providerStarted) {
    startDataSource();
  }

  if (g_ctx.providerHealth == ProviderHealth::Unavailable) {
    g_display.renderStatus(StatusMessage::DataSourceUnavailable);
    return;
  }

  if (!g_ctx.hasLatestAircraft) {
    g_display.renderStatus(StatusMessage::WaitingForFirstData);
    return;
  }

  if (g_ctx.latestAircraft.count == 0) {
    if (ntpTimeIsPlausible()) {
      const time_t now = time(nullptr);
      const struct tm* t = localtime(&now);
      char timeBuf[6];
      std::snprintf(timeBuf, sizeof(timeBuf), "%02d:%02d", t->tm_hour, t->tm_min);
      g_display.renderIdleClock(timeBuf, g_ctx.wifiState == WifiState::Connected, g_ctx.providerStarted);
    } else {
      g_display.renderStatus(StatusMessage::NoMatchingAircraft);
    }
    return;
  }

  const AircraftState& nearest = g_ctx.latestAircraft.items[0];
  if (ntpTimeIsPlausible() && isStalePosition(nearest.positionTimestampMs, static_cast<int64_t>(time(nullptr)) * 1000)) {
    g_display.renderStatus(StatusMessage::DataIsStale);
    return;
  }

  const uint32_t ageSeconds = (millis() - g_ctx.lastAircraftUpdateAtMs) / 1000;
  g_display.renderSingleAircraft(nearest, ageSeconds);
}

}  // namespace

void setup() {
  auto cfg = M5.config();
  M5.begin(cfg);
  g_display.begin();
  g_display.renderBoot();

  LittleFS.begin(/*formatOnFail=*/true);

  if (!getDeviceId(g_ctx.deviceId, sizeof(g_ctx.deviceId))) {
    std::strcpy(g_ctx.deviceId, "core2-unknown");
  }

  g_ctx.hasConfig = g_ctx.configStore.loadConfig(g_ctx.config);
  g_ctx.hasPairingToken = g_ctx.configStore.loadPairingToken(g_ctx.pairingToken, sizeof(g_ctx.pairingToken));

  WifiCredentials creds;
  if (loadWifiCredentials(creds) && connectToWifi(creds, kWifiConnectTimeoutMs)) {
    enterConnectedMode();
  } else {
    enterProvisioningMode();
  }
}

void loop() {
  g_display.update();

  if (g_ctx.wifiState == WifiState::Provisioning) {
    processProvisioningDns();

    if (!g_wifiJustSaved && millis() - g_lastWifiCredsCheckAtMs >= kWifiCredsCheckIntervalMs) {
      g_lastWifiCredsCheckAtMs = millis();
      WifiCredentials creds;
      if (loadWifiCredentials(creds)) {
        g_wifiJustSaved = true;
        g_wifiJustSavedAtMs = millis();
      }
    } else if (g_wifiJustSaved && millis() - g_wifiJustSavedAtMs > kWifiSaveRebootDelayMs) {
      ESP.restart();
    }
    return;
  }

  if (millis() - g_lastRenderAtMs >= kRenderIntervalMs) {
    renderCurrentState();
    g_lastRenderAtMs = millis();
  }
}