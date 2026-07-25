#include "app/display.h"

#include <M5Unified.h>
#include <qrcode.h>

#include <cstdio>
#include <cstring>

namespace ofd::app {

// File-scope context pointer for battery access in footer rendering.
// Set once by main.cpp after the context is constructed.
// NOT static — main.cpp references this via extern.
AppContext* s_ctx = nullptr;

namespace {

constexpr int kScreenW = 320;
constexpr int kScreenH = 240;

// Footer bar
constexpr int kFooterH = 22;
constexpr int kFooterY = kScreenH - kFooterH;
constexpr uint32_t kFooterBg = 0x0841;

// Age colours
constexpr uint32_t kAgeFreshMs = 5000;
constexpr uint32_t kAgeStaleMs = 30000;
constexpr uint32_t kAgeColourFresh = 0x07E0;
constexpr uint32_t kAgeColourWarn = 0xFFE0;
constexpr uint32_t kAgeColourCrit = 0xF800;

// Design tokens
constexpr uint32_t kTextPrimary   = TFT_WHITE;
constexpr uint32_t kTextLabel     = 0x8410;  // cool grey
constexpr uint32_t kTextAccent    = 0x0A84FF >> 3; // approximate blue on TFT
constexpr uint32_t kTextDim       = 0x5280;
constexpr uint32_t kSepColour     = 0x3186;

uint32_t ageColour(uint32_t ageMs) {
  if (ageMs <= kAgeFreshMs) return kAgeColourFresh;
  if (ageMs <= kAgeStaleMs) return kAgeColourWarn;
  return kAgeColourCrit;
}

const char* bearingToCompass(double deg) {
  if (deg < 0.0 || deg >= 360.0) return "—";
  const char* dirs[] = {"N","NNE","NE","ENE","E","ESE","SE","SSE",
                         "S","SSW","SW","WSW","W","WNW","NW","NNW"};
  return dirs[static_cast<int>((deg + 11.25) / 22.5) % 16];
}

void drawQrCode(const char* text, int ox, int oy, int ms) {
  QRCode qrcode;
  uint8_t buf[qrcode_getBufferSize(6)];
  qrcode_initText(&qrcode, buf, 6, ECC_MEDIUM, text);
  for (uint8_t y = 0; y < qrcode.size; y++)
    for (uint8_t x = 0; x < qrcode.size; x++)
      M5.Display.fillRect(ox + x * ms, oy + y * ms, ms, ms,
                           qrcode_getModule(&qrcode, x, y) ? TFT_BLACK : TFT_WHITE);
}

void fillRect(int x, int y, int w, int h, uint32_t c) { M5.Display.fillRect(x, y, w, h, c); }
void dot(int x, int y, int r, uint32_t c)             { M5.Display.fillCircle(x, y, r, c); }

// Small pill label
void pill(int x, int y, const char* label, bool active) {
  M5.Display.setTextSize(1);
  const uint32_t bg = active ? 0x0300 : 0x3000;
  const uint32_t fg = active ? TFT_GREEN : TFT_RED;
  const int w = static_cast<int>(std::strlen(label)) * 6 + 12;
  fillRect(x, y, w, 14, bg);
  M5.Display.setTextColor(fg, bg);
  M5.Display.setCursor(x + 4, y + 3);
  M5.Display.print(label);
}

// Print a metric row: label (size 1, left) and value (size 2, right)
void metricRow(int xL, int xR, int y, const char* label, const char* value, uint32_t valColour) {
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(xL, y);
  M5.Display.print(label);

  M5.Display.setTextSize(2);
  M5.Display.setTextColor(valColour, TFT_BLACK);
  // Right-align approximate: 12px per char at size 2, 2px padding
  const int vw = static_cast<int>(std::strlen(value)) * 12;
  M5.Display.setCursor(xR - vw, y + 2);
  M5.Display.print(value);
}

// Print a value with unit, returning the x coordinate of the end of the text.
// Used for concatenated metrics like "425 kt / 489 mph"
void printValUnit(int x, int y, const char* val, const char* unit) {
  M5.Display.setTextSize(2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setCursor(x, y);
  M5.Display.print(val);
  const int vw = static_cast<int>(std::strlen(val)) * 12;
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(x + vw + 3, y + 5);
  M5.Display.print(unit);
}

void printClipped(const char* s, int maxChars) {
  char buf[48];
  const size_t len = std::strlen(s);
  if (len > static_cast<size_t>(maxChars)) {
    std::memcpy(buf, s, maxChars);
    buf[maxChars] = '\0';
    M5.Display.print(buf);
  } else {
    M5.Display.print(s);
  }
}

}  // namespace

// ---- public interface ----

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
  M5.Display.setCursor(60, 80);
  M5.Display.print("OpenFlightDisplay");
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(100, 120);
  M5.Display.print("Initializing...");
}

void Display::renderProvisioning(const char* apName) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setCursor(40, 40);
  M5.Display.print("Wi‑Fi Setup");
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(40, 80);
  M5.Display.print("Connect your phone to:");
  M5.Display.drawRoundRect(20, 105, kScreenW - 40, 50, 6, kSepColour);
  M5.Display.setTextSize(2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  char ssidBuf[20]; std::strncpy(ssidBuf, apName, 14); ssidBuf[14]='\0';
  M5.Display.setCursor(35, 118);
  M5.Display.print(ssidBuf);
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(40, 180);
  M5.Display.print("Then open 192.168.4.1");
}

void Display::renderPairingReady(const char* ip, const char* code) {
  M5.Display.fillScreen(TFT_WHITE);
  char url[64]; std::snprintf(url, sizeof(url), "http://%s/pair?code=%s", ip, code);
  drawQrCode(url, 12, 30, 4);
  M5.Display.setTextColor(TFT_BLACK, TFT_WHITE);
  M5.Display.setTextSize(1);
  M5.Display.setCursor(180, 32); M5.Display.print("Browse to:");
  M5.Display.setTextSize(2);
  char ipBuf[20]; std::strncpy(ipBuf, ip, 15); ipBuf[15]='\0';
  M5.Display.setCursor(180, 56); M5.Display.print(ipBuf);
  M5.Display.setCursor(180, 84); M5.Display.print("/setup");
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(0x7BEF, TFT_WHITE);
  M5.Display.setCursor(180, 210); M5.Display.print("Scan QR or type manually");
}

void Display::renderSingleAircraft(const ofd::AircraftState& ac, uint32_t ageS) {
  M5.Display.fillScreen(TFT_BLACK);

  const uint32_t dotColour = ageColour(ageS * 1000);

  // ---- top identity line ----
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(3);
  M5.Display.setCursor(6, 6);
  if (ac.hasCallsign) {
    printClipped(ac.callsign, 14);
  } else {
    M5.Display.print(ac.icaoHex);
  }

  // Age dot (top right)
  dot(kScreenW - 14, 14, 6, dotColour);

  // Airline name (below callsign, size 1)
  M5.Display.setTextSize(1);
  if (ac.hasAirlineName) {
    M5.Display.setTextColor(kTextLabel, TFT_BLACK);
    M5.Display.setCursor(8, 44);
    printClipped(ac.airlineName, 24);
  }

  // Aircraft type badge (right side, inline with airline)
  if (ac.hasAircraftType) {
    M5.Display.setTextColor(kTextAccent, TFT_BLACK);
    M5.Display.setCursor(kScreenW - 70, 44);
    M5.Display.print(ac.aircraftTypeCode);
  }

  // ---- metric rows ----
  const int row1Y = 65;
  const int rowGap = 36;
  const int xL = 8;
  const int xR = kScreenW - 8;

  // Row 1: DISTANCE
  {
    char buf[24];
    if (ac.hasDistanceFromObserverKm) std::snprintf(buf, sizeof(buf), "%.1f km", ac.distanceFromObserverKm);
    else std::snprintf(buf, sizeof(buf), "--");
    metricRow(xL, xR, row1Y, "DISTANCE", buf, TFT_WHITE);
  }

  // Row 2: ALTITUDE
  {
    char buf[24];
    if (ac.hasAltitudeFt) std::snprintf(buf, sizeof(buf), "%.0f ft", ac.altitudeFt);
    else std::snprintf(buf, sizeof(buf), "-- ft");
    metricRow(xL, xR, row1Y + rowGap, "ALTITUDE", buf, TFT_WHITE);
  }

  // Row 3: SPEED (knots + mph)
  {
    M5.Display.setTextSize(1);
    M5.Display.setTextColor(kTextLabel, TFT_BLACK);
    M5.Display.setCursor(xL, row1Y + rowGap * 2);
    M5.Display.print("SPEED");

    if (ac.hasGroundSpeedKt) {
      M5.Display.setTextSize(2);
      M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
      char ktBuf[12], mphBuf[12];
      std::snprintf(ktBuf, sizeof(ktBuf), "%.0f", ac.groundSpeedKt);
      std::snprintf(mphBuf, sizeof(mphBuf), "%.0f", ac.groundSpeedMph);
      const int ktW = static_cast<int>(std::strlen(ktBuf)) * 12;
      M5.Display.setCursor(xR - ktW - 42, row1Y + rowGap * 2 + 2);
      M5.Display.print(ktBuf);
      M5.Display.setTextSize(1);
      M5.Display.setTextColor(kTextLabel, TFT_BLACK);
      M5.Display.setCursor(xR - 42, row1Y + rowGap * 2 + 6);
      M5.Display.print("kt");
      M5.Display.setTextSize(2);
      M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
      M5.Display.setCursor(xR - 24, row1Y + rowGap * 2 + 2);
      M5.Display.print(" / ");
      M5.Display.print(mphBuf);
      M5.Display.setTextSize(1);
      M5.Display.setTextColor(kTextLabel, TFT_BLACK);
      M5.Display.print(" mph");
    } else {
      metricRow(xL, xR, row1Y + rowGap * 2, "SPEED", "--", TFT_WHITE);
    }
  }

  // Row 4: HEADING
  {
    char buf[24];
    if (ac.hasTrackHeadingDeg) {
      const char* comp = bearingToCompass(ac.trackHeadingDeg);
      std::snprintf(buf, sizeof(buf), "%.0f° %s", ac.trackHeadingDeg, comp);
    } else {
      std::snprintf(buf, sizeof(buf), "--");
    }
    // Adjust label position slightly since speed row may be taller
    const int row4Y = row1Y + rowGap * 3;
    M5.Display.setTextSize(1);
    M5.Display.setTextColor(kTextLabel, TFT_BLACK);
    M5.Display.setCursor(xL, row4Y);
    M5.Display.print("HEADING");
    M5.Display.setTextSize(2);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    const int vw = static_cast<int>(std::strlen(buf)) * 12;
    M5.Display.setCursor(xR - vw, row4Y + 2);
    M5.Display.print(buf);
  }

  // ---- footer ----
  fillRect(0, kFooterY, kScreenW, kFooterH, kFooterBg);
  pill(4, kFooterY + 4, "WIFI", true);
  pill(64, kFooterY + 4, "ADS-B", true);

  // Battery pill (far right of footer, size 1)
  {
    M5.Display.setTextSize(1);
    const auto& b = s_ctx != nullptr ? s_ctx->battery : ofd::BatteryState{};
    if (b.valid) {
      uint32_t batBg, batFg;
      if (b.percent >= 20)      { batBg = 0x0300; batFg = TFT_GREEN; }
      else if (b.percent >= 10) { batBg = 0x3220; batFg = 0xFFE0; }
      else                       { batBg = 0x3000; batFg = TFT_RED; }
      char batLabel[8];
      if (b.charging) std::snprintf(batLabel, sizeof(batLabel), "%u%% CHG", b.percent);
      else std::snprintf(batLabel, sizeof(batLabel), "%u%%", b.percent);
      const int bw = static_cast<int>(std::strlen(batLabel)) * 6 + 12;
      fillRect(190, kFooterY + 4, bw, 14, batBg);
      M5.Display.setTextColor(batFg, batBg);
      M5.Display.setCursor(194, kFooterY + 7);
      M5.Display.print(batLabel);
    }
  }

  // Vertical rate indicator in footer
  M5.Display.setTextSize(1);
  if (ac.hasVerticalRateFtPerMin) {
    const char* arrow = "=";
    uint32_t vc = kTextLabel;
    if (ac.verticalRateFtPerMin > 500)      { arrow = "\x18"; vc = kAgeColourFresh; }
    else if (ac.verticalRateFtPerMin < -500) { arrow = "\x19"; vc = kAgeColourCrit; }
    M5.Display.setTextColor(vc, kFooterBg);
    M5.Display.setCursor(130, kFooterY + 4);
    M5.Display.printf("%s %.0f fpm", arrow, ac.verticalRateFtPerMin >= 0 ? ac.verticalRateFtPerMin : -ac.verticalRateFtPerMin);
  }

  // Emergency indicator (right side of footer)
  if (ac.emergencyState != ofd::EmergencyState::None) {
    M5.Display.setTextColor(TFT_RED, kFooterBg);
    M5.Display.setCursor(240, kFooterY + 4);
    M5.Display.print("EMERGENCY");
  } else if (ac.onGround) {
    M5.Display.setTextColor(kTextLabel, kFooterBg);
    M5.Display.setCursor(250, kFooterY + 4);
    M5.Display.print("GND");
  }
}

void Display::renderStatus(StatusMessage message, const char* ip) {
  M5.Display.fillScreen(TFT_BLACK);

  const char* label = "";
  switch (message) {
    case StatusMessage::WaitingForFirstData:   label = "Connecting to adsb.lol..."; break;
    case StatusMessage::NoMatchingAircraft:    label = "No aircraft in range";     break;
    case StatusMessage::DataSourceUnavailable: label = "Data source unavailable";   break;
    case StatusMessage::WifiDisconnected:      label = "Wi‑Fi disconnected";        break;
    case StatusMessage::ConfigurationRequired: label = "Setup required";            break;
    case StatusMessage::DataIsStale:           label = "Position data is stale";   break;
  }

  M5.Display.drawRoundRect(20, 70, kScreenW - 40, 90, 6, kSepColour);
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setCursor(35, 95);
  printClipped(label, 28);

  if (ip && ip[0]) {
    M5.Display.setTextColor(kTextLabel, TFT_BLACK);
    M5.Display.setCursor(10, kFooterY - 14);
    M5.Display.printf("IP: %s", ip);
  }

  // Footer pill
  fillRect(0, kFooterY, kScreenW, kFooterH, kFooterBg);
  pill(4, kFooterY + 4, "WIFI", message != StatusMessage::WifiDisconnected);
}

void Display::renderIdleClock(const char* timeHhMm, bool wifiUp, bool provUp) {
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextSize(5);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setCursor(80, 65);
  M5.Display.print(timeHhMm);
  M5.Display.drawFastHLine(80, 145, 160, kSepColour);
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(kTextLabel, TFT_BLACK);
  M5.Display.setCursor(95, 160);
  M5.Display.print("Scanning for aircraft");
  fillRect(0, kFooterY, kScreenW, kFooterH, kFooterBg);
  pill(4, kFooterY + 4, "WIFI", wifiUp);
  pill(64, kFooterY + 4, "ADS-B", provUp);
}

void Display::renderOtaProgress(uint8_t percent, bool complete, const char* status) {
  M5.Display.fillScreen(TFT_BLACK);

  // Title
  M5.Display.setTextSize(2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  const char* title = complete ? "Update Complete" : "Updating Firmware";
  M5.Display.setCursor(40, 30);
  M5.Display.print(title);

  // Status text
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(0x8410, TFT_BLACK);
  M5.Display.setCursor(40, 65);
  M5.Display.print(status);

  if (!complete) {
    // Progress bar background
    const int bx = 40, by = 95, bw = 240, bh = 20;
    M5.Display.drawRoundRect(bx, by, bw, bh, 4, 0x3186);

    // Progress fill
    if (percent > 0) {
      const int fillW = (bw - 4) * percent / 100;
      M5.Display.fillRoundRect(bx + 2, by + 2, fillW, bh - 4, 2, 0x0A84FF >> 3);
    }

    // Percentage text
    M5.Display.setTextSize(2);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    char pct[8];
    std::snprintf(pct, sizeof(pct), "%u%%", percent);
    M5.Display.setCursor(140, 130);
    M5.Display.print(pct);
  }

  // Warning
  M5.Display.setTextSize(1);
  M5.Display.setTextColor(TFT_RED, TFT_BLACK);
  M5.Display.setCursor(40, complete ? 130 : 180);
  M5.Display.print(complete ? "Restarting..." : "Do not remove power");
}

void Display::update() { M5.update(); }

}  // namespace ofd::app