#include <ESPAsyncWebServer.h>
#include <ESPmDNS.h>
#include <LittleFS.h>
#include <M5Unified.h>
#include <WiFi.h>
#include <time.h>

#include "app/app_context.h"
#include "app/config_store.h"
#include "app/device_identity.h"
#include "app/display.h"
#include "app/gateway_client.h"
#include "app/pairing_server.h"
#include "app/wifi_provisioning.h"
#include "domain/staleness.h"

using namespace ofd;
using namespace ofd::app;

namespace {

AsyncWebServer g_server(80);
AppContext g_ctx;
Display g_display;
GatewayClient g_gatewayClient;

uint32_t g_wifiConnectAttemptAtMs = 0;
bool g_wifiJustSaved = false;
uint32_t g_wifiJustSavedAtMs = 0;
uint32_t g_lastPairingCodeRegenAtMs = 0;

constexpr uint32_t kWifiConnectTimeoutMs = 15'000;
constexpr uint32_t kWifiSaveRebootDelayMs = 1500;

// loop() runs continuously with no inherent delay; redrawing the full
// screen on every iteration would flicker and burn SPI bandwidth for no
// benefit (docs/CORE2_HARDWARE.md's "rate-limited redraws" requirement).
// A fixed interval is a deliberately simple choice for Phase 1 --
// selective/partial invalidation is left for when there's more than one
// screen region to reason about (Phase 2+).
constexpr uint32_t kRenderIntervalMs = 500;
uint32_t g_lastRenderAtMs = 0;

// True once NTP has plausibly synced (used to gate aircraft
// position-staleness checks, which need real wall-clock time --
// connection/update-recency checks below use millis() uptime instead
// and don't need this).
bool ntpTimeIsPlausible() {
  return time(nullptr) > 1'700'000'000;  // 2023-11-14, well before this project existed
}

void enterProvisioningMode() {
  char apName[32];
  std::snprintf(apName, sizeof(apName), "OpenFlightDisplay-Setup-%s", g_ctx.deviceId + 6);
  startProvisioningAccessPoint(g_server, apName);
  g_ctx.wifiState = WifiState::Provisioning;
  g_display.renderProvisioning(apName);
}

void enterConnectedMode() {
  g_ctx.wifiState = WifiState::Connected;
  configTime(0, 0, "pool.ntp.org", "time.nist.gov");
  MDNS.begin(g_ctx.deviceId);
  MDNS.addService("openflightdisplay", "tcp", 80);

  registerPairingRoutes(g_server, g_ctx);
  g_server.begin();

  if (!g_ctx.hasPairingToken) {
    g_ctx.pairingCodeManager.regenerate(millis());
    g_lastPairingCodeRegenAtMs = millis();
    g_display.renderPairingReady(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
  } else if (g_ctx.hasConfig && g_ctx.config.hasGatewayUrl) {
    g_gatewayClient.begin(g_ctx);
  }
}

void renderCurrentState() {
  if (g_ctx.wifiState == WifiState::Provisioning) return;  // provisioning screen already shown

  if (!g_ctx.hasPairingToken) {
    // Pairing screen stays up; regenerate the code if it expired so the
    // display never gets stuck showing a dead code.
    if (millis() - g_lastPairingCodeRegenAtMs > PairingCodeManager::kExpiryMs) {
      g_ctx.pairingCodeManager.regenerate(millis());
      g_lastPairingCodeRegenAtMs = millis();
      g_display.renderPairingReady(WiFi.localIP().toString().c_str(), g_ctx.pairingCodeManager.currentCode());
    }
    return;
  }

  if (!g_ctx.hasConfig || !g_ctx.config.hasMonitoringArea || !g_ctx.config.hasGatewayUrl) {
    g_display.renderStatus(StatusMessage::ConfigurationRequired);
    return;
  }

  if (g_ctx.gatewayState != GatewayConnectionState::Connected) {
    g_display.renderStatus(StatusMessage::DataSourceUnavailable);
    return;
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
    g_display.renderStatus(StatusMessage::NoMatchingAircraft);
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

    // wifi_provisioning.cpp's POST handler saves credentials but never
    // reboots from inside an async callback (so the HTTP response can
    // finish flushing first); poll for the saved file here instead.
    if (!g_wifiJustSaved) {
      WifiCredentials creds;
      if (loadWifiCredentials(creds)) {
        g_wifiJustSaved = true;
        g_wifiJustSavedAtMs = millis();
      }
    } else if (millis() - g_wifiJustSavedAtMs > kWifiSaveRebootDelayMs) {
      ESP.restart();
    }
    return;
  }

  if (g_ctx.hasPairingToken && g_ctx.hasConfig && g_ctx.config.hasGatewayUrl) {
    g_gatewayClient.loop();
  }

  if (millis() - g_lastRenderAtMs >= kRenderIntervalMs) {
    renderCurrentState();
    g_lastRenderAtMs = millis();
  }
}
