#include "app/display.h"

#include <M5Unified.h>
#include <WiFi.h>
#include <qrcode.h>

#include <cstdio>
#include <cstring>

#include "app/ui_layout.h"
#include "app/ui_theme.h"
#include "domain/display_format.h"

namespace ofd::app {

// File-scope context pointer -- see display.h for why this exists instead
// of a constructor argument (avoids a circular header dependency with
// AppContext, which several app-layer modules also depend on).
AppContext* s_ctx = nullptr;

namespace {

using namespace ofd::app::theme;
using namespace ofd::app::layout;
using lgfx::textdatum_t;

// ---- persistent sprite ----
//
// Only the header (320x31, ~19.8KB as RGB565) is a persistent sprite.
// The body is drawn directly to the panel with a single region-clear
// per redraw (never a full fillScreen). See docs/CORE2_DISPLAY.md
// "Sprite and buffering strategy" for the memory reasoning: this
// specific Core2 unit has confirmed no PSRAM (docs/CORE2_HARDWARE.md),
// and a full 320x240 sprite (~150KB) plus TLS/JSON buffers used
// elsewhere in the firmware was judged too large a permanent
// reservation to make without a measured free-heap baseline from real
// hardware. Redraws happen at most every few seconds (kRenderIntervalMs
// in main.cpp), so a direct region-clear-and-redraw has no visible
// flicker at that cadence -- the flicker in the previous implementation
// came from calling fillScreen() across the *entire* panel including
// the header on every redraw, not from the absence of a body sprite.
M5Canvas g_header(&M5.Display);
bool g_headerReady = false;

void ensureHeaderSprite() {
  if (g_headerReady) return;
  g_header.setPsram(false);  // no PSRAM on this hardware; force internal RAM
  g_header.setColorDepth(16);
  g_header.createSprite(kScreenW, kHeaderH + 1);
  g_headerReady = true;
}

// ---- text fit helpers ----
//
// Every piece of dynamic text is measured with the font's own
// textWidth(), never estimated from character count. `fallbackFont` may
// be nullptr to skip straight to ellipsizing at `primaryFont`.
void drawFitText(lgfx::LGFXBase& gfx, const char* text, int x, int y, int maxWidth,
                  const lgfx::IFont* primaryFont, const lgfx::IFont* fallbackFont, uint16_t color,
                  uint16_t bg, textdatum_t datum) {
  gfx.setTextDatum(datum);
  gfx.setTextColor(color, bg);

  gfx.setFont(primaryFont);
  if (gfx.textWidth(text) <= maxWidth) {
    gfx.drawString(text, x, y);
    return;
  }

  const lgfx::IFont* font = primaryFont;
  if (fallbackFont != nullptr) {
    gfx.setFont(fallbackFont);
    font = fallbackFont;
    if (gfx.textWidth(text) <= maxWidth) {
      gfx.drawString(text, x, y);
      return;
    }
  }

  // Ellipsize at whichever font is currently set.
  char buf[48];
  std::strncpy(buf, text, sizeof(buf) - 1);
  buf[sizeof(buf) - 1] = '\0';
  size_t len = std::strlen(buf);
  char withEllipsis[52];
  while (len > 0) {
    buf[len] = '\0';
    std::snprintf(withEllipsis, sizeof(withEllipsis), "%s\xE2\x80\xA6", buf);  // "…"
    if (gfx.textWidth(withEllipsis) <= maxWidth) {
      gfx.drawString(withEllipsis, x, y);
      return;
    }
    len--;
  }
  gfx.drawString("\xE2\x80\xA6", x, y);
  (void)font;
}

void setMicroFont(lgfx::LGFXBase& gfx, uint8_t size) {
  gfx.setFont(&fonts::Font0);
  gfx.setTextSize(size);
}

// ---- small icon primitives ----

void drawWifiIcon(lgfx::LGFXBase& gfx, int x, int y, bool connected) {
  const uint16_t barColor = connected ? COLOR_GOOD : COLOR_GRID;
  // Three ascending signal bars.
  gfx.fillRect(x, y + 8, 3, 4, barColor);
  gfx.fillRect(x + 5, y + 5, 3, 7, barColor);
  gfx.fillRect(x + 10, y + 1, 3, 11, barColor);
  if (!connected) {
    // Diagonal slash -- unambiguous "offline" treatment without red
    // alarm-coding a routine LAN condition in the header.
    gfx.drawLine(x, y, x + kHeaderWifiIconW - 1, y + kHeaderWifiIconH - 1, COLOR_CRITICAL);
    gfx.drawLine(x + 1, y, x + kHeaderWifiIconW - 1, y + kHeaderWifiIconH - 2, COLOR_CRITICAL);
  }
}

void drawBatteryIcon(lgfx::LGFXBase& gfx, int x, int y, const ofd::BatteryViewModel& batt) {
  const uint16_t outline = COLOR_TEXT_SECONDARY;
  gfx.drawRect(x, y, kHeaderBatteryIconW, kHeaderBatteryIconH, outline);
  gfx.fillRect(x + kHeaderBatteryIconW, y + 3, kHeaderBatteryNubW, kHeaderBatteryIconH - 6, outline);

  if (!batt.known) {
    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
    gfx.setTextDatum(textdatum_t::middle_center);
    gfx.setTextColor(COLOR_TEXT_DIM, COLOR_HEADER_BG);
    gfx.drawString("?", x + kHeaderBatteryIconW / 2, y + kHeaderBatteryIconH / 2);
    return;
  }

  const int innerW = kHeaderBatteryIconW - 4;
  const int fillW = (innerW * batt.percent) / 100;
  const uint16_t fillColor = colorForRole(batt.colorRole);
  if (fillW > 0) {
    gfx.fillRect(x + 2, y + 2, fillW, kHeaderBatteryIconH - 4, fillColor);
  }

  if (batt.charging) {
    // Minimal bolt: two overlapping triangles rendered as short lines.
    const int cx = x + kHeaderBatteryIconW / 2;
    const int cy = y + kHeaderBatteryIconH / 2;
    gfx.drawLine(cx + 1, y + 2, cx - 2, cy, COLOR_TEXT_PRIMARY);
    gfx.drawLine(cx - 2, cy, cx + 1, cy, COLOR_TEXT_PRIMARY);
    gfx.drawLine(cx + 1, cy, cx - 1, y + kHeaderBatteryIconH - 2, COLOR_TEXT_PRIMARY);
  }
}

// Small hollow-circle degree mark, drawn as a primitive instead of
// relying on a font glyph -- the bundled GFXFF fonts only cover ASCII
// 0x20-0x7E and have no degree sign. Positioned just after the last
// digit, aligned near the cap height rather than the baseline.
void drawDegreeMark(lgfx::LGFXBase& gfx, int x, int yTop, uint16_t color) {
  gfx.drawCircle(x + 3, yTop + 3, 2, color);
}

// ---- header ----

void drawHeader(const char* title) {
  ensureHeaderSprite();

  ofd::BatteryViewModel batt;
  ofd::buildBatteryViewModel(s_ctx != nullptr ? s_ctx->battery : ofd::BatteryState{}, batt);
  const bool wifiConnected = s_ctx != nullptr && s_ctx->wifiConnected;

  g_header.fillSprite(COLOR_HEADER_BG);

  setMicroFont(g_header, FONT_MICRO_GLCD_SIZE_HEADER);
  drawFitText(g_header, title, kHeaderTitleX, kHeaderTitleY, kHeaderTitleMaxRightX - kHeaderTitleX, &fonts::Font0,
              nullptr, COLOR_TEXT_PRIMARY, COLOR_HEADER_BG, textdatum_t::top_left);

  drawWifiIcon(g_header, kHeaderWifiIconLeftX, 9, wifiConnected);
  drawBatteryIcon(g_header, kHeaderBatteryIconLeftX, 9, batt);

  setMicroFont(g_header, FONT_MICRO_GLCD_SIZE_LABEL);
  g_header.setTextDatum(textdatum_t::top_right);
  g_header.setTextColor(colorForRole(batt.colorRole), COLOR_HEADER_BG);
  g_header.drawString(batt.percentText, kHeaderBatteryPercentRightX, 12);

  g_header.drawFastHLine(0, kHeaderAccentLineY, kScreenW, COLOR_ACCENT);

  g_header.pushSprite(0, 0);
}

// Full remaining height below the header -- used by the pre-operational
// screens (boot/provisioning/setup-required/OTA) that have no tab bar.
void clearBody() {
  M5.Display.fillRect(0, kHeaderH + 1, kScreenW, kScreenH - kHeaderH - 1, COLOR_BACKGROUND);
}

// Only up to the tab bar -- used by every operational-state screen (see
// drawTabBar below), which all reserve the bottom kTabBarH for page
// navigation.
void clearOperationalBody() {
  M5.Display.fillRect(0, kHeaderH + 1, kScreenW, kTabBarY - kHeaderH - 1, COLOR_BACKGROUND);
}

const char* tabLabel(DetailPage page) {
  switch (page) {
    case DetailPage::Flight: return "FLIGHT";
    case DetailPage::Detail: return "DETAIL";
    case DetailPage::System: return "SYSTEM";
  }
  return "FLIGHT";
}

// Bottom tab bar -- BtnA/BtnB/BtnC map directly to these 3 columns (see
// main.cpp's loop()). Present on every operational-state screen so
// navigation always works the same way regardless of what's currently
// shown, per docs/CORE2_DISPLAY.md.
void drawTabBar() {
  auto& gfx = M5.Display;
  const DetailPage current = s_ctx != nullptr ? s_ctx->currentPage : DetailPage::Flight;

  gfx.fillRect(0, kTabBarY, kScreenW, kTabBarH, COLOR_HEADER_BG);
  gfx.drawFastHLine(0, kTabBarY, kScreenW, COLOR_GRID);
  gfx.drawFastVLine(kTabBarColBoundaries[1], kTabBarY, kTabBarH, COLOR_GRID);
  gfx.drawFastVLine(kTabBarColBoundaries[2], kTabBarY, kTabBarH, COLOR_GRID);

  const DetailPage pages[3] = {DetailPage::Flight, DetailPage::Detail, DetailPage::System};
  for (int i = 0; i < 3; i++) {
    const int colX = kTabBarColBoundaries[i];
    const int colW = kTabBarColBoundaries[i + 1] - colX;
    const bool active = pages[i] == current;

    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
    gfx.setTextDatum(textdatum_t::middle_center);
    gfx.setTextColor(active ? COLOR_ACCENT : COLOR_TEXT_SECONDARY, COLOR_HEADER_BG);
    gfx.drawString(tabLabel(pages[i]), colX + colW / 2, kTabBarY + kTabBarH / 2);

    if (active) {
      gfx.fillRect(colX + 1, kTabBarY, colW - 2, 2, COLOR_ACCENT);
    }
  }
}

// ---- generic full-screen status body (searching/no-traffic/errors) ----

void drawStatusBody(const char* title, const char* body, const char* footnote) {
  auto& gfx = M5.Display;
  gfx.setFont(FONT_VALUE_LARGE());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.drawString(title, kScreenW / 2, kStatusTitleY);

  if (body != nullptr && body[0] != '\0') {
    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_HEADER);
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
    gfx.drawString(body, kScreenW / 2, kStatusBodyY);
  }

  if (footnote != nullptr && footnote[0] != '\0') {
    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
    gfx.drawString(footnote, kScreenW / 2, kStatusFootnoteY);
  }
}

// ---- detail / system page rows (simple label/value list) ----

// Both label and value use the small bitmap font at the same size --
// distinguished by color only, not size. This is a deliberate change
// from an earlier version that used a larger GFXFF font for the value:
// that font's own line height (~22px) left no room before the next
// row started, and its width overflowed the value column for long
// content (SSIDs up to 32 chars, "47.6062, -122.3321"-style
// coordinates) -- found by testing on real hardware, where the value
// text visibly overran into neighboring rows. The bitmap font's fixed
// 6px/char width at this size guarantees every value in practice fits
// or ellipsizes cleanly instead of overlapping.
void drawDetailRow(int rowIndex, const char* label, const char* value, uint16_t valueColor) {
  auto& gfx = M5.Display;
  const int y = kDetailTop + rowIndex * kDetailRowH;

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(label, kDetailLabelX, y);

  const int maxW = kScreenW - kDetailValueX - kIdentityRightMarginX;
  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  drawFitText(gfx, value, kDetailValueX, y, maxW, &fonts::Font0, nullptr, valueColor, COLOR_BACKGROUND,
              textdatum_t::top_left);
}

// ---- operational grid cell ----

struct CellRect {
  int x, y, w, h;
};

CellRect gridCell(int col, int row) {
  CellRect r;
  r.x = kGridColBoundaries[col];
  r.w = kGridColBoundaries[col + 1] - r.x;
  r.y = kGridTop + row * kGridRowH;
  r.h = kGridRowH;
  return r;
}

void drawGridFrame() {
  auto& gfx = M5.Display;
  gfx.drawFastVLine(kGridColBoundaries[1], kGridTop, kGridBottom - kGridTop, COLOR_GRID);
  gfx.drawFastVLine(kGridColBoundaries[2], kGridTop, kGridBottom - kGridTop, COLOR_GRID);
  gfx.drawFastHLine(0, kGridTop + kGridRowH, kScreenW, COLOR_GRID);
}

void drawCellLabel(const CellRect& cell, const char* label) {
  auto& gfx = M5.Display;
  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(label, cell.x + kGridCellPadX, cell.y + kGridLabelOffsetY);
}

// Value + inline unit, left-aligned, auto-fit within the cell.
void drawCellValueWithUnit(const CellRect& cell, const char* value, const char* unit, uint16_t color) {
  auto& gfx = M5.Display;
  const int maxW = cell.w - kGridCellPadX * 2;
  const int x = cell.x + kGridCellPadX;
  const int y = cell.y + kGridValueOffsetY;

  gfx.setFont(FONT_VALUE_LARGE());
  gfx.setTextColor(color, COLOR_BACKGROUND);
  gfx.setTextDatum(textdatum_t::top_left);
  const int valueW = gfx.textWidth(value);

  if (unit != nullptr && unit[0] != '\0') {
    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
    const int unitW = gfx.textWidth(unit);
    if (valueW + 4 + unitW <= maxW) {
      gfx.setFont(FONT_VALUE_LARGE());
      gfx.setTextColor(color, COLOR_BACKGROUND);
      gfx.drawString(value, x, y);
      setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
      gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
      gfx.drawString(unit, x + valueW + 4, y + 9);
      return;
    }
  }

  // Unit doesn't fit inline (or there isn't one) -- value alone, with the
  // unit (if any) stacked on its own small caption line beneath.
  drawFitText(gfx, value, x, y, maxW, FONT_VALUE_LARGE(), FONT_VALUE_SMALL(), color, COLOR_BACKGROUND,
              textdatum_t::top_left);
  if (unit != nullptr && unit[0] != '\0') {
    setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
    gfx.setTextDatum(textdatum_t::top_left);
    gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
    gfx.drawString(unit, x, cell.y + kGridCaptionOffsetY);
  }
}

void drawTrackCell(const CellRect& cell, const ofd::AircraftViewModel& vm) {
  auto& gfx = M5.Display;
  const int x = cell.x + kGridCellPadX;
  const int y = cell.y + kGridValueOffsetY;

  if (!vm.hasTrack) {
    drawFitText(gfx, "\xE2\x80\x94", x, y, cell.w - kGridCellPadX * 2, FONT_VALUE_LARGE(), nullptr,
                COLOR_TEXT_PRIMARY, COLOR_BACKGROUND, textdatum_t::top_left);
    return;
  }

  gfx.setFont(FONT_VALUE_LARGE());
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.drawString(vm.trackDegrees, x, y);
  const int degreesW = gfx.textWidth(vm.trackDegrees);
  drawDegreeMark(gfx, x + degreesW + 2, y, COLOR_TEXT_PRIMARY);

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(vm.trackCompass, x, cell.y + kGridCaptionOffsetY);
}

void drawStatusCell(const CellRect& cell, const ofd::AircraftViewModel& vm) {
  auto& gfx = M5.Display;
  const uint16_t color = colorForRole(ofd::displayStatusColorRole(vm.status));
  const char* word = ofd::displayStatusWord(vm.status);

  // Colored left accent bar instead of a filled badge/card -- a clean
  // separator-based treatment per docs/CORE2_DISPLAY.md rather than a
  // decorative panel.
  gfx.fillRect(cell.x, cell.y + kGridLabelOffsetY, 3, cell.h - kGridLabelOffsetY - 8, color);

  const int x = cell.x + kGridCellPadX + 4;
  const int maxW = cell.w - kGridCellPadX * 2 - 4;
  drawFitText(gfx, word, x, cell.y + kGridValueOffsetY, maxW, FONT_VALUE_LARGE(), FONT_VALUE_SMALL(), color,
              COLOR_BACKGROUND, textdatum_t::top_left);
}

// ---- identity block ----

void drawIdentityBlock(const ofd::AircraftViewModel& vm) {
  auto& gfx = M5.Display;

  const uint16_t callsignColor = vm.callsignIsPlaceholder ? COLOR_TEXT_SECONDARY : COLOR_TEXT_PRIMARY;
  drawFitText(gfx, vm.callsign, kIdentityLeftX, kCallsignY, kScreenW - kIdentityLeftX - kIdentityRightMarginX,
              FONT_IDENTIFIER_PRIMARY(), FONT_IDENTIFIER_FALLBACK(), callsignColor, COLOR_BACKGROUND,
              textdatum_t::top_left);

  if (vm.hasAirline) {
    drawFitText(gfx, vm.airlineName, kIdentityLeftX, kAirlineTypeY + 4, kAirlineMaxWidth, FONT_LABEL_REGULAR(),
                nullptr, COLOR_TEXT_SECONDARY, COLOR_BACKGROUND, textdatum_t::top_left);
  }

  gfx.drawRect(kTypeBadgeX, kAirlineTypeY, kTypeBadgeW, kAirlineTypeH, COLOR_GRID);
  gfx.setFont(FONT_VALUE_SMALL());
  gfx.setTextDatum(textdatum_t::middle_center);
  gfx.setTextColor(COLOR_ACCENT, COLOR_BACKGROUND);
  gfx.drawString(vm.aircraftType, kTypeBadgeX + kTypeBadgeW / 2, kAirlineTypeY + kAirlineTypeH / 2);

  char icaoLine[16];
  std::snprintf(icaoLine, sizeof(icaoLine), "ICAO %s", vm.icao);
  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString(icaoLine, kIdentityLeftX, kIcaoY + 2);

  // Data-freshness caption, right-aligned on the ICAO line.
  char ageText[8];
  ofd::formatDataAge(vm.ageSeconds, vm.stale, ageText, sizeof(ageText));
  const uint16_t ageColor = vm.stale ? COLOR_CAUTION : COLOR_TEXT_DIM;
  gfx.setTextDatum(textdatum_t::top_right);
  gfx.setTextColor(ageColor, COLOR_BACKGROUND);
  gfx.drawString(ageText, kScreenW - kIdentityRightMarginX, kIcaoY + 2);

  gfx.drawFastHLine(0, kIdentityDividerY, kScreenW, COLOR_GRID);
}

}  // namespace

// ---- public interface ----

void Display::begin() {
  M5.Display.setRotation(1);
  M5.Display.setBrightness(200);
  M5.Display.setTextWrap(false);
  M5.Display.fillScreen(COLOR_BACKGROUND);
  ensureHeaderSprite();
}

void Display::renderBoot(const char* firmwareVersion) {
  clearBody();
  drawHeader("OPEN FLIGHT DISPLAY");

  auto& gfx = M5.Display;
  gfx.setFont(FONT_IDENTIFIER_FALLBACK());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.drawString("OPENFLIGHT", kScreenW / 2, kStatusTitleY);

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_HEADER);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("INITIALIZING", kScreenW / 2, kStatusBodyY);

  char version[24];
  std::snprintf(version, sizeof(version), "FIRMWARE %s", firmwareVersion);
  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString(version, kScreenW / 2, kStatusFootnoteY);
}

void Display::renderProvisioning(const char* apName) {
  clearBody();
  drawHeader("WI-FI SETUP");

  auto& gfx = M5.Display;
  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_HEADER);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("CONNECT YOUR PHONE TO:", kScreenW / 2, kStatusTitleY - 20);

  gfx.drawRoundRect(kStatusMargin, kStatusTitleY + 6, kScreenW - kStatusMargin * 2, 44, 4, COLOR_GRID);
  drawFitText(gfx, apName, kScreenW / 2, kStatusTitleY + 18, kScreenW - kStatusMargin * 2 - 16,
              FONT_VALUE_LARGE(), FONT_VALUE_SMALL(), COLOR_TEXT_PRIMARY, COLOR_BACKGROUND,
              textdatum_t::top_center);

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString("THEN OPEN 192.168.4.1", kScreenW / 2, kStatusFootnoteY);
}

void Display::renderLocationRequired(const char* ipAddress, const char* pairingCode) {
  clearBody();
  drawHeader("SETUP REQUIRED");

  char url[64];
  std::snprintf(url, sizeof(url), "http://%s/pair?code=%s", ipAddress, pairingCode);

  QRCode qrcode;
  uint8_t qrBuf[qrcode_getBufferSize(6)];
  qrcode_initText(&qrcode, qrBuf, 6, ECC_MEDIUM, url);
  constexpr int kModuleSize = 4;
  const int qrPx = qrcode.size * kModuleSize;
  const int qrX = 14;
  const int qrY = kHeaderH + (kScreenH - kHeaderH - qrPx) / 2;
  M5.Display.fillRect(qrX - 4, qrY - 4, qrPx + 8, qrPx + 8, COLOR_TEXT_PRIMARY);
  for (uint8_t y = 0; y < qrcode.size; y++) {
    for (uint8_t x = 0; x < qrcode.size; x++) {
      if (qrcode_getModule(&qrcode, x, y)) {
        M5.Display.fillRect(qrX + x * kModuleSize, qrY + y * kModuleSize, kModuleSize, kModuleSize,
                             COLOR_BACKGROUND);
      }
    }
  }

  auto& gfx = M5.Display;
  const int textX = qrX + qrPx + 24;
  const int maxW = kScreenW - textX - kIdentityRightMarginX;

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("BROWSE TO", textX, 46);

  drawFitText(gfx, ipAddress, textX, 60, maxW, FONT_VALUE_LARGE(), FONT_VALUE_SMALL(), COLOR_TEXT_PRIMARY,
              COLOR_BACKGROUND, textdatum_t::top_left);

  gfx.setFont(FONT_VALUE_SMALL());
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("/setup", textX, 92);

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString("SCAN QR OR TYPE MANUALLY", textX, 130);
}

void Display::renderAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  clearOperationalBody();
  drawHeader("NEAREST AIRCRAFT");
  drawIdentityBlock(vm);
  drawGridFrame();

  drawCellLabel(gridCell(0, 0), "DIST");
  drawCellValueWithUnit(gridCell(0, 0), vm.hasDistance ? vm.distanceValue : "\xE2\x80\x94",
                        vm.hasDistance ? ofd::AircraftViewModel::kDistanceUnit : "", COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(1, 0), "ALT");
  drawCellValueWithUnit(gridCell(1, 0), vm.hasAltitude ? vm.altitudeValue : "\xE2\x80\x94",
                        (vm.hasAltitude && !vm.altitudeIsGround) ? ofd::AircraftViewModel::kAltitudeUnit : "",
                        COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(2, 0), "SPEED");
  drawCellValueWithUnit(gridCell(2, 0), vm.hasSpeed ? vm.speedValue : "\xE2\x80\x94",
                        vm.hasSpeed ? ofd::AircraftViewModel::kSpeedUnit : "", COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(0, 1), "TRACK");
  drawTrackCell(gridCell(0, 1), vm);

  drawCellLabel(gridCell(1, 1), "V/S");
  drawCellValueWithUnit(gridCell(1, 1), vm.hasVerticalRate ? vm.verticalRateValue : "\xE2\x80\x94",
                        vm.hasVerticalRate ? ofd::AircraftViewModel::kVerticalRateUnit : "", COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(2, 1), "STATUS");
  drawStatusCell(gridCell(2, 1), vm);

  drawTabBar();
}

void Display::renderSearching() {
  clearOperationalBody();
  drawHeader("NEAREST AIRCRAFT");
  drawStatusBody("SEARCHING FOR AIRCRAFT", "CONNECTING TO ADS-B DATA SOURCE", nullptr);
  drawTabBar();
}

void Display::renderNoTraffic(bool hasClock, const char* timeHhMm) {
  clearOperationalBody();
  drawHeader("NEAREST AIRCRAFT");
  drawStatusBody("NO NEARBY AIRCRAFT", "MONITORING WITHIN CONFIGURED RADIUS", nullptr);

  if (hasClock && timeHhMm != nullptr && timeHhMm[0] != '\0') {
    auto& gfx = M5.Display;
    gfx.setFont(FONT_VALUE_LARGE());
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
    gfx.drawString(timeHhMm, kScreenW / 2, kStatusFootnoteY - 10);
  }

  drawTabBar();
}

void Display::renderWifiOffline() {
  clearOperationalBody();
  drawHeader("NEAREST AIRCRAFT");
  drawStatusBody("WI-FI OFFLINE", "ATTEMPTING TO RECONNECT", nullptr);
  drawTabBar();
}

void Display::renderApiError() {
  clearOperationalBody();
  drawHeader("NEAREST AIRCRAFT");
  drawStatusBody("ADS-B DATA UNAVAILABLE", "DATA SOURCE UNREACHABLE", nullptr);
  drawTabBar();
}

void Display::renderAircraftDetail(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  clearOperationalBody();
  drawHeader("FLIGHT DETAIL");

  char ageText[8];
  ofd::formatDataAge(vm.ageSeconds, vm.stale, ageText, sizeof(ageText));
  const uint16_t ageColor = vm.stale ? COLOR_CAUTION : COLOR_TEXT_PRIMARY;

  // No literal degree sign here (the GFXFF value font only covers ASCII
  // 0x20-0x7E -- same reason drawTrackCell draws a circle primitive
  // instead of a glyph on the Flight page). "247 WSW" reads unambiguously
  // without it in a single-line label/value row.
  char bearingText[16];
  if (vm.hasBearing) {
    std::snprintf(bearingText, sizeof(bearingText), "%s %s", vm.bearingDegrees, vm.bearingCompass);
  } else {
    std::strcpy(bearingText, "\xE2\x80\x94");
  }

  int row = 0;
  drawDetailRow(row++, "CALLSIGN", vm.callsign, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "TYPE", vm.aircraftType, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "ICAO", vm.icao, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "SQUAWK", vm.squawk, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "POSITION", vm.position, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "BEARING", bearingText, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "DATA AGE", ageText, ageColor);

  drawTabBar();
}

void Display::renderDetailPlaceholder() {
  clearOperationalBody();
  drawHeader("FLIGHT DETAIL");
  drawStatusBody("NO AIRCRAFT DATA", "SWITCH TO THE FLIGHT TAB FOR STATUS", nullptr);
  drawTabBar();
}

void Display::renderSystemInfo() {
  clearOperationalBody();
  drawHeader("SYSTEM");

  const bool wifiConnected = s_ctx != nullptr && s_ctx->wifiConnected;
  char wifiText[40];
  if (wifiConnected) {
    std::snprintf(wifiText, sizeof(wifiText), "CONNECTED (%s)", WiFi.SSID().c_str());
  } else {
    std::strcpy(wifiText, "OFFLINE");
  }

  const char* providerText = "OK";
  uint16_t providerColor = COLOR_GOOD;
  if (s_ctx != nullptr) {
    switch (s_ctx->providerHealth) {
      case ofd::ProviderHealth::Ok: providerText = "OK"; providerColor = COLOR_GOOD; break;
      case ofd::ProviderHealth::Degraded: providerText = "DEGRADED"; providerColor = COLOR_CAUTION; break;
      case ofd::ProviderHealth::Unavailable: providerText = "UNAVAILABLE"; providerColor = COLOR_CRITICAL; break;
    }
  }

  ofd::BatteryViewModel batt;
  ofd::buildBatteryViewModel(s_ctx != nullptr ? s_ctx->battery : ofd::BatteryState{}, batt);
  char batteryText[24];
  if (batt.known) {
    std::snprintf(batteryText, sizeof(batteryText), "%s (%.2fV)%s", batt.percentText, s_ctx->battery.voltage,
                  batt.charging ? " CHG" : "");
  } else {
    std::strcpy(batteryText, "\xE2\x80\x94");
  }

  const uint32_t upSeconds = millis() / 1000;
  char uptimeText[16];
  std::snprintf(uptimeText, sizeof(uptimeText), "%02u:%02u:%02u", static_cast<unsigned>(upSeconds / 3600),
                static_cast<unsigned>((upSeconds / 60) % 60), static_cast<unsigned>(upSeconds % 60));

  char ipText[20];
  std::strncpy(ipText, wifiConnected ? WiFi.localIP().toString().c_str() : "\xE2\x80\x94", sizeof(ipText) - 1);
  ipText[sizeof(ipText) - 1] = '\0';

  int row = 0;
  drawDetailRow(row++, "WI-FI", wifiText, wifiConnected ? COLOR_TEXT_PRIMARY : COLOR_CRITICAL);
  drawDetailRow(row++, "IP ADDRESS", ipText, COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "DATA SOURCE", "ADS-B (ADSB.LOL)", COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "PROVIDER", providerText, providerColor);
  drawDetailRow(row++, "BATTERY", batteryText, colorForRole(batt.colorRole));
  drawDetailRow(row++, "DEVICE ID", s_ctx != nullptr ? s_ctx->deviceId : "\xE2\x80\x94", COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "FIRMWARE", s_ctx != nullptr ? s_ctx->firmwareVersion : "\xE2\x80\x94", COLOR_TEXT_PRIMARY);
  drawDetailRow(row++, "UPTIME", uptimeText, COLOR_TEXT_PRIMARY);

  drawTabBar();
}

void Display::renderOtaProgress(uint8_t percent, bool complete, const char* status) {
  clearBody();
  drawHeader("FIRMWARE UPDATE");

  auto& gfx = M5.Display;
  gfx.setFont(FONT_VALUE_LARGE());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.drawString(complete ? "UPDATE COMPLETE" : "UPDATING FIRMWARE", kScreenW / 2, kStatusTitleY - 20);

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_HEADER);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(status, kScreenW / 2, kStatusTitleY + 14);

  if (!complete) {
    const int bx = kStatusMargin, by = kStatusTitleY + 44, bw = kScreenW - kStatusMargin * 2, bh = 22;
    gfx.drawRoundRect(bx, by, bw, bh, 4, COLOR_GRID);
    if (percent > 0) {
      const int fillW = (bw - 4) * percent / 100;
      gfx.fillRoundRect(bx + 2, by + 2, fillW, bh - 4, 2, COLOR_ACCENT);
    }
    char pct[8];
    std::snprintf(pct, sizeof(pct), "%u%%", percent);
    gfx.setFont(FONT_VALUE_LARGE());
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
    gfx.drawString(pct, kScreenW / 2, by + bh + 12);
  }

  setMicroFont(gfx, FONT_MICRO_GLCD_SIZE_LABEL);
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_CRITICAL, COLOR_BACKGROUND);
  gfx.drawString(complete ? "RESTARTING..." : "DO NOT REMOVE POWER", kScreenW / 2, kStatusFootnoteY);
}

void Display::update() { M5.update(); }

}  // namespace ofd::app
