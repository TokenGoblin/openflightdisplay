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

constexpr uint32_t kWifiConnectTimeoutMs = 15000;
constexpr uint32_t kWifiSaveRebootDelayMs = 1500;
constexpr uint32_t kRenderIntervalMs = 5000;
constexpr uint32_t kOtaHandleIntervalMs = 200;
constexpr uint32_t kWifiStatusCheckIntervalMs = 2000;
// A stale-but-still-shown aircraft (docs/CORE2_DISPLAY.md's "preserve,
// don't hide" rule) is only kept on screen up to this long past its last
// update. Well beyond kStalePositionThresholdMs so a normal short gap
// between polls never triggers it -- this is specifically for "the
// provider's been reporting Ok health but we haven't actually heard
// about this aircraft in minutes," at which point showing it as if it
// might still be nearby would be actively misleading.
constexpr uint32_t kSuperStaleMs = static_cast<uint32_t>(ofd::kStalePositionThresholdMs) * 5;

uint32_t g_lastRenderAtMs = 0;
uint32_t g_lastOtaAtMs = 0;
uint32_t g_lastWifiStatusCheckAtMs = 0;

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
  std::snprintf(apName, sizeof(apName), "OFD-Setup-%s", g_ctx.deviceId + 6);
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
  g_ctx.wifiConnected = true;
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
    g_display.renderLocationRequired(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
  } else {
    startDataSource();
  }
}

void renderCurrentState() {
  if (g_ctx.wifiState == WifiState::Provisioning) return;

  if (!g_ctx.wifiConnected) {
    g_display.renderWifiOffline();
    return;
  }

  if (!g_ctx.hasConfig || !g_ctx.config.hasMonitoringArea) {
    if (millis() - g_lastPairingCodeRegenAtMs > PairingCodeManager::kExpiryMs) {
      g_ctx.pairingCodeManager.regenerate(millis());
      g_lastPairingCodeRegenAtMs = millis();
    }
    g_display.renderLocationRequired(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
    return;
  }

  if (!g_ctx.providerStarted) {
    startDataSource();
  }

  // SYSTEM tab is always renderable regardless of aircraft/provider
  // state -- in fact it's most useful exactly when something else here
  // is wrong, so it takes priority over the provider-health/aircraft
  // checks below rather than being blocked by them.
  if (g_ctx.currentPage == DetailPage::System) {
    g_display.renderSystemInfo();
    return;
  }

  if (g_ctx.providerHealth == ProviderHealth::Unavailable) {
    g_display.renderApiError();
    return;
  }

  if (!g_ctx.hasLatestAircraft) {
    g_display.renderSearching();
    return;
  }

  const uint32_t sinceUpdateMs = millis() - g_ctx.lastAircraftUpdateAtMs;

  if (g_ctx.latestAircraft.count == 0) {
    if (sinceUpdateMs > kSuperStaleMs) {
      g_display.renderApiError();
      return;
    }
    if (g_ctx.currentPage == DetailPage::Detail) {
      g_display.renderDetailPlaceholder();
      return;
    }
    const bool hasClock = ntpTimeIsPlausible();
    char timeBuf[6] = {0};
    if (hasClock) {
      const time_t now = time(nullptr);
      const struct tm* t = localtime(&now);
      std::snprintf(timeBuf, sizeof(timeBuf), "%02d:%02d", t->tm_hour, t->tm_min);
    }
    g_display.renderNoTraffic(hasClock, timeBuf);
    return;
  }

  const AircraftState& nearest = g_ctx.latestAircraft.items[0];
  const bool stale = ntpTimeIsPlausible() &&
                      isStalePosition(nearest.positionTimestampMs, static_cast<int64_t>(time(nullptr)) * 1000);

  if (stale && sinceUpdateMs > kSuperStaleMs) {
    // Stale for far longer than one missed poll -- stop showing a
    // possibly very old aircraft and fall back to "still searching"
    // instead, per docs/CORE2_DISPLAY.md's staleness rule (preserve
    // briefly, don't preserve indefinitely).
    if (g_ctx.currentPage == DetailPage::Detail) {
      g_display.renderDetailPlaceholder();
      return;
    }
    g_display.renderSearching();
    return;
  }

  const uint32_t ageSeconds = sinceUpdateMs / 1000;
  if (g_ctx.currentPage == DetailPage::Detail) {
    g_display.renderAircraftDetail(nearest, ageSeconds, stale);
  } else {
    g_display.renderAircraft(nearest, ageSeconds, stale);
  }
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
  g_display.renderBoot(g_ctx.firmwareVersion);

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

    // Reboot only once the setup form has actually been submitted --
    // NOT merely because a credentials file exists on disk. A device
    // that was previously configured and is simply out of range of its
    // saved network also ends up here with a perfectly real wifi.json
    // already present; treating "file exists" as "just saved" would
    // reboot in a tight loop every ~1.5s forever instead of ever
    // showing the setup portal (found by actually testing this on
    // hardware away from the originally-paired network).
    if (!g_wifiJustSaved && consumeWifiCredentialsJustSaved()) {
      g_wifiJustSaved = true;
      g_wifiJustSavedAtMs = millis();
    } else if (g_wifiJustSaved && millis() - g_wifiJustSavedAtMs > kWifiSaveRebootDelayMs) {
      ESP.restart();
    }
    return;
  }

  // Bottom-button page navigation: BtnA/BtnB/BtnC each jump directly to
  // a specific page (FLIGHT/DETAIL/SYSTEM) rather than prev/next, so a
  // given physical button always means the same thing. M5.update()
  // (called via g_display.update() at the top of loop()) is what
  // actually refreshes M5.BtnX's pressed state from the touch panel.
  // Re-renders immediately on a page change instead of waiting up to
  // kRenderIntervalMs so button presses feel responsive.
  DetailPage requestedPage = g_ctx.currentPage;
  if (M5.BtnA.wasPressed()) requestedPage = DetailPage::Flight;
  if (M5.BtnB.wasPressed()) requestedPage = DetailPage::Detail;
  if (M5.BtnC.wasPressed()) requestedPage = DetailPage::System;
  if (requestedPage != g_ctx.currentPage) {
    g_ctx.currentPage = requestedPage;
    renderCurrentState();
    g_lastRenderAtMs = millis();
  }

  // Detect a Wi-Fi drop after the initial connect -- WiFi.status() is
  // cheap, so this just needs throttling to avoid spamming it every
  // loop() iteration. ESP32 Arduino's WiFi does not auto-reconnect on
  // its own, hence the explicit WiFi.reconnect() call.
  if (millis() - g_lastWifiStatusCheckAtMs >= kWifiStatusCheckIntervalMs) {
    g_lastWifiStatusCheckAtMs = millis();
    const bool nowConnected = WiFi.status() == WL_CONNECTED;
    if (g_ctx.wifiConnected && !nowConnected) {
      Serial.println("WiFi link dropped -- attempting reconnect");
      WiFi.reconnect();
    }
    g_ctx.wifiConnected = nowConnected;
  }

  // Poll battery in the background (~10 s interval, throttled internally)
  pollBattery(g_ctx);

  if (millis() - g_lastRenderAtMs >= kRenderIntervalMs) {
    renderCurrentState();
    g_lastRenderAtMs = millis();
  }
}