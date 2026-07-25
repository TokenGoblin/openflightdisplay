#include <ArduinoOTA.h>
#include <ESPAsyncWebServer.h>
#include <ESPmDNS.h>
#include <LittleFS.h>
#include <M5Unified.h>
#include <WiFi.h>
#include <time.h>

#include <cstring>

#include "app/adsb_provider.h"
#include "app/app_context.h"
#include "app/battery_monitor.h"
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
constexpr uint32_t kRenderIntervalMs = 5000;
constexpr uint32_t kOtaHandleIntervalMs = 200;

uint32_t g_lastRenderAtMs = 0;
uint32_t g_lastOtaAtMs = 0;

bool g_otaInProgress = false;
uint8_t g_otaPercent = 0;

bool ntpTimeIsPlausible() {
  return time(nullptr) > 1700000000;
}

void initOta() {
  ArduinoOTA.setHostname(g_ctx.deviceId);
  ArduinoOTA.setPassword(OTA_PASSWORD);

  ArduinoOTA.onStart([&]() {
    g_otaInProgress = true;
    g_otaPercent = 0;
    Serial.println("OTA update started");
    g_display.renderOtaProgress(0, false, "Receiving firmware...");
  });

  ArduinoOTA.onProgress([&](unsigned int progress, unsigned int total) {
    if (total > 0) {
      const uint8_t pct = static_cast<uint8_t>((static_cast<uint64_t>(progress) * 100) / total);
      if (pct != g_otaPercent) {
        g_otaPercent = pct;
        Serial.printf("OTA: %u%%\n", g_otaPercent);
        char status[32];
        std::snprintf(status, sizeof(status), "%.1f / %.1f kB",
                      progress / 1024.0f, total / 1024.0f);
        g_display.renderOtaProgress(g_otaPercent, false, status);
      }
    }
  });

  ArduinoOTA.onEnd([&]() {
    g_otaInProgress = false;
    Serial.println("OTA update complete");
    g_display.renderOtaProgress(100, true, "Update installed");
    delay(2000);
  });

  ArduinoOTA.onError([&](ota_error_t error) {
    g_otaInProgress = false;
    Serial.printf("OTA error: %u\n", error);
    const char* errMsg = "Unknown error";
    switch (error) {
      case OTA_AUTH_ERROR:    errMsg = "Authentication failed"; break;
      case OTA_BEGIN_ERROR:   errMsg = "Could not start update"; break;
      case OTA_CONNECT_ERROR: errMsg = "Connection failed"; break;
      case OTA_RECEIVE_ERROR: errMsg = "Receive failed"; break;
      case OTA_END_ERROR:     errMsg = "Finalisation failed"; break;
      default: break;
    }
    char buf[40];
    std::snprintf(buf, sizeof(buf), "Error: %s", errMsg);
    g_display.renderOtaProgress(g_otaPercent, false, buf);
    delay(5000);
  });

  ArduinoOTA.begin();
  Serial.println("OTA service started");
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
  MDNS.addServiceTxt("openflightdisplay", "tcp", "type", "core2");

  registerPairingRoutes(g_server, g_ctx);
  g_server.begin();

  initOta();

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

// Wire the display's file-scope context pointer so the battery
// pill can read the cached BatteryState from AppContext.
// s_ctx is defined in display.cpp namespace ofd::app.
using ofd::app::s_ctx;

void setup() {
  auto cfg = M5.config();
  M5.begin(cfg);
  g_display.begin();
  s_ctx = &g_ctx;
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

  if (g_ctx.wifiState == WifiState::Connected) {
    if (g_otaInProgress || millis() - g_lastOtaAtMs >= kOtaHandleIntervalMs) {
      ArduinoOTA.handle();
      g_lastOtaAtMs = millis();
    }
  }

  if (g_otaInProgress) return;

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

  // Poll battery in the background (~10 s interval, throttled internally)
  pollBattery(g_ctx);

  if (millis() - g_lastRenderAtMs >= kRenderIntervalMs) {
    renderCurrentState();
    g_lastRenderAtMs = millis();
  }
}