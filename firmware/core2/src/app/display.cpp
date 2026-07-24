#include "app/display.h"

#include <M5Unified.h>
#include <qrcode.h>

#include <cstdio>

namespace ofd::app {

namespace {

void drawQrCode(const char* text, int originX, int originY, int moduleSizePx) {
  QRCode qrcode;
  uint8_t qrcodeBytes[qrcode_getBufferSize(6)];
  qrcode_initText(&qrcode, qrcodeBytes, 6, ECC_MEDIUM, text);
  for (uint8_t y = 0; y < qrcode.size; y++) {
    for (uint8_t x = 0; x < qrcode.size; x++) {
      const uint16_t color = qrcode_getModule(&qrcode, x, y) ? TFT_BLACK : TFT_WHITE;
      M5.Display.fillRect(originX + x * moduleSizePx, originY + y * moduleSizePx, moduleSizePx, moduleSizePx,
                           color);
    }
  }
}

}  // namespace

void Display::begin() {
  M5.Display.setRotation(1);
  M5.Display.setBrightness(200);
  M5.Display.setTextWrap(false);
  M5.Display.fillScreen(TFT_BLACK);
}

void Display::renderBoot() {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(20, 100);
  M5.Display.print("OpenFlightDisplay");
  M5.Display.setTextSize(1);
  M5.Display.setCursor(20, 130);
  M5.Display.print("Starting...");
}

void Display::renderProvisioning(const char* apName) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(10, 20);
  M5.Display.print("Wi-Fi setup needed");
  M5.Display.setTextSize(1);
  M5.Display.setCursor(10, 60);
  M5.Display.print("Connect to Wi-Fi network:");
  M5.Display.setTextSize(2);
  M5.Display.setCursor(10, 80);
  M5.Display.print(apName);
  M5.Display.setTextSize(1);
  M5.Display.setCursor(10, 120);
  M5.Display.print("Then browse to 192.168.4.1");
}

void Display::renderPairingReady(const char* ipAddress, const char* pairingCode) {
  M5.Display.fillScreen(TFT_WHITE);
  char url[64];
  std::snprintf(url, sizeof(url), "http://%s/pair?code=%s", ipAddress, pairingCode);
  drawQrCode(url, 20, 20, 4);

  // Verified on real hardware: the QR code doesn't reliably work either
  // way (phone camera apps open it as a dead link; in-app scanning needs
  // HTTPS, which this LAN-over-HTTP system doesn't have -- see
  // useQrScanner.ts) -- manual entry is the one path that actually works,
  // so it's what this screen leads with now.
  M5.Display.setTextColor(TFT_BLACK, TFT_WHITE);
  M5.Display.setTextSize(1);
  M5.Display.setCursor(180, 30);
  M5.Display.print("Enter in the app:");
  M5.Display.setTextSize(2);
  M5.Display.setCursor(180, 65);
  M5.Display.print(ipAddress);
  M5.Display.setCursor(180, 90);
  M5.Display.print("Code:");
  M5.Display.setCursor(180, 110);
  M5.Display.print(pairingCode);
}

void Display::renderSingleAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);

  M5.Display.setTextSize(3);
  M5.Display.setCursor(10, 10);
  M5.Display.print(aircraft.hasCallsign ? aircraft.callsign : aircraft.icaoHex);

  M5.Display.setTextSize(2);
  M5.Display.setCursor(10, 60);
  if (aircraft.hasDistanceFromObserverKm) {
    M5.Display.printf("%.1f km", aircraft.distanceFromObserverKm);
  }
  if (aircraft.hasBearingFromObserverDeg) {
    M5.Display.printf("  %03.0f deg", aircraft.bearingFromObserverDeg);
  }

  M5.Display.setCursor(10, 90);
  if (aircraft.hasAltitudeFt) {
    M5.Display.printf("Alt %.0f ft", aircraft.altitudeFt);
  }
  if (aircraft.hasVerticalRateFtPerMin) {
    const char* trend = aircraft.verticalRateFtPerMin > 100    ? " UP"
                        : aircraft.verticalRateFtPerMin < -100 ? " DOWN"
                                                                 : " LEVEL";
    M5.Display.print(trend);
  }

  M5.Display.setCursor(10, 120);
  if (aircraft.hasGroundSpeedKt) {
    M5.Display.printf("%.0f kt", aircraft.groundSpeedKt);
  }

  M5.Display.setTextSize(1);
  M5.Display.setCursor(10, 220);
  M5.Display.printf("updated %us ago", static_cast<unsigned>(ageSeconds));
}

void Display::renderStatus(StatusMessage message, const char* ipAddress) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(10, 100);

  switch (message) {
    case StatusMessage::WaitingForFirstData:
      M5.Display.print("Waiting for first data");
      break;
    case StatusMessage::NoMatchingAircraft:
      M5.Display.print("No matching aircraft");
      break;
    case StatusMessage::DataSourceUnavailable:
      M5.Display.print("Data source unavailable");
      break;
    case StatusMessage::WifiDisconnected:
      M5.Display.print("Wi-Fi disconnected");
      break;
    case StatusMessage::ConfigurationRequired:
      M5.Display.print("Configuration required");
      break;
    case StatusMessage::DataIsStale:
      M5.Display.print("Data is stale");
      break;
  }

  if (ipAddress != nullptr && ipAddress[0] != '\0') {
    M5.Display.setTextSize(1);
    M5.Display.setCursor(10, 220);
    M5.Display.printf("Device IP: %s", ipAddress);
  }
}

void Display::renderIdleClock(const char* timeHhMm, bool wifiConnected, bool gatewayConnected) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(4);
  M5.Display.setCursor(80, 80);
  M5.Display.print(timeHhMm);

  M5.Display.setTextSize(1);
  M5.Display.setCursor(10, 220);
  M5.Display.printf("Wi-Fi: %s  Gateway: %s", wifiConnected ? "up" : "down", gatewayConnected ? "up" : "down");
}

void Display::update() {
  M5.update();
  // Phase 1's only touch interaction is deferred to Phase 2 (compact
  // list navigation needs more than one screen to make swipe/tap
  // gestures meaningful -- see docs/FEATURE_PARITY_MATRIX.md). M5.update()
  // is still called every loop so touch/button state doesn't go stale
  // for when that lands.
}

}  // namespace ofd::app
