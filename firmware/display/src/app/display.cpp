#include "app/display.h"

#include <M5Unified.h>
#include <WiFi.h>
#include <qrcode.h>

#include <cstdio>
#include <cstring>

#include "app/display_draw.h"
#include "app/ui_layout.h"
#include "app/ui_theme.h"
#include "board/board.h"
#include "domain/display_format.h"

namespace ofd::app {

// File-scope context pointer -- see display.h for why this exists instead
// of a constructor argument (avoids a circular header dependency with
// AppContext, which several app-layer modules also depend on).
AppContext* s_ctx = nullptr;

namespace draw {

using namespace ofd::app::theme;
using namespace ofd::app::layout;

namespace {

// ---- buffering ----
//
// Two strategies, chosen by board::kUseFullScreenCanvas:
//
//   Full-screen canvas (Tab5). The whole frame is composed off-screen in
//   PSRAM and pushed once. At 1280x720 a region-clear-and-redraw would be
//   a very visible wipe, and this board has 32MB of PSRAM against a
//   ~1.84MB buffer.
//
//   Header sprite + direct body draw (Core2). Only the header (320x31,
//   ~19.8KB as RGB565) is a persistent sprite; the body is drawn straight
//   to the panel with a single region-clear per redraw, never a full
//   fillScreen. That board has confirmed no PSRAM
//   (docs/CORE2_HARDWARE.md), and a full 320x240 sprite (~150KB) on top
//   of the TLS/JSON buffers used elsewhere was judged too large a
//   permanent reservation without a measured free-heap baseline. Redraws
//   happen at most every few seconds, so a direct region-clear has no
//   visible flicker at that cadence -- the flicker an earlier version had
//   came from calling fillScreen() across the *entire* panel including
//   the header on every redraw, not from the absence of a body sprite.
//
// See docs/DISPLAY_UI.md "Sprite and buffering strategy".
//
// Both objects are declared unconditionally (an M5Canvas that never gets
// createSprite() called on it costs nothing but the object itself) so
// neither strategy needs #ifdefs threaded through the drawing code.
M5Canvas g_frameCanvas(&M5.Display);
bool g_frameCanvasReady = false;
M5Canvas g_headerSprite(&M5.Display);
bool g_headerSpriteReady = false;

void setupBuffers() {
  if (board::kUseFullScreenCanvas) {
    g_frameCanvas.setPsram(true);
    g_frameCanvas.setColorDepth(16);
    g_frameCanvasReady = g_frameCanvas.createSprite(kScreenW, kScreenH) != nullptr;
    if (g_frameCanvasReady) return;
    // Falling back rather than failing outright: a device that renders
    // with a visible wipe is still a usable device, and a blank panel
    // with no explanation is the worst possible outcome of a PSRAM
    // allocation that didn't go as planned.
    Serial.printf("[display] full-screen canvas alloc failed (%dx%d, ~%u KB) -- falling back to direct draw\n",
                  kScreenW, kScreenH, static_cast<unsigned>((kScreenW * kScreenH * 2) / 1024));
  }

  g_headerSprite.setPsram(false);  // small enough to want internal RAM regardless
  g_headerSprite.setColorDepth(16);
  g_headerSpriteReady = g_headerSprite.createSprite(kScreenW, kHeaderH + 1) != nullptr;
}

// ---- icon primitives ----

void drawWifiIcon(lgfx::LGFXBase& g, int x, int y, bool connected) {
  const uint16_t barColor = connected ? COLOR_GOOD : COLOR_GRID;
  const int bottom = y + kHeaderWifiIconH;
  const int heights[3] = {kHeaderWifiBarH1, kHeaderWifiBarH2, kHeaderWifiBarH3};
  for (int i = 0; i < 3; i++) {
    g.fillRect(x + i * kHeaderWifiBarStep, bottom - heights[i], kHeaderWifiBarW, heights[i], barColor);
  }

  if (!connected) {
    // Diagonal slash -- unambiguous "offline" treatment without
    // red-alarm-coding a routine LAN condition in the header.
    const int slashThickness = kHeaderWifiIconW / 12 < 2 ? 2 : kHeaderWifiIconW / 12;
    for (int i = 0; i < slashThickness; i++) {
      g.drawLine(x + i, y, x + i + kHeaderWifiIconW - 1, y + kHeaderWifiIconH - 1, COLOR_CRITICAL);
    }
  }
}

void drawBatteryIcon(lgfx::LGFXBase& g, int x, int y, const ofd::BatteryViewModel& batt) {
  const uint16_t outline = COLOR_TEXT_SECONDARY;
  g.drawRect(x, y, kHeaderBatteryIconW, kHeaderBatteryIconH, outline);
  g.fillRect(x + kHeaderBatteryIconW, y + kHeaderBatteryNubInsetY, kHeaderBatteryNubW,
             kHeaderBatteryIconH - kHeaderBatteryNubInsetY * 2, outline);

  if (!batt.known) {
    applyFont(g, FONT_MICRO_LABEL());
    g.setTextDatum(textdatum_t::middle_center);
    g.setTextColor(COLOR_TEXT_DIM, COLOR_HEADER_BG);
    g.drawString("?", x + kHeaderBatteryIconW / 2, y + kHeaderBatteryIconH / 2);
    return;
  }

  const int innerW = kHeaderBatteryIconW - kHeaderBatteryFillInset * 2;
  const int fillW = (innerW * batt.percent) / 100;
  if (fillW > 0) {
    g.fillRect(x + kHeaderBatteryFillInset, y + kHeaderBatteryFillInset, fillW,
               kHeaderBatteryIconH - kHeaderBatteryFillInset * 2, colorForRole(batt.colorRole));
  }

  if (batt.charging) {
    // Minimal bolt: three short strokes, thickened by redrawing them
    // offset so it stays visible as the icon scales up between boards.
    const int cx = x + kHeaderBatteryIconW / 2;
    const int cy = y + kHeaderBatteryIconH / 2;
    const int t = kHeaderBatteryBoltThickness;
    for (int i = 0; i < t; i++) {
      g.drawLine(cx + t + i, y + 2 * t, cx - 2 * t + i, cy, COLOR_TEXT_PRIMARY);
      g.drawLine(cx - 2 * t + i, cy, cx + t + i, cy, COLOR_TEXT_PRIMARY);
      g.drawLine(cx + t + i, cy, cx - t + i, y + kHeaderBatteryIconH - 2 * t, COLOR_TEXT_PRIMARY);
    }
  }
}

// Small hollow-circle degree mark, drawn as a primitive instead of
// relying on a font glyph -- the bundled GFXFF fonts only cover ASCII
// 0x20-0x7E and have no degree sign. Positioned just after the last
// digit, aligned near the cap height rather than the baseline.
void drawDegreeMark(lgfx::LGFXBase& g, int x, int yTop, uint16_t color) {
  g.drawCircle(x + kDegreeMarkOffsetX, yTop + kDegreeMarkOffsetY, kDegreeMarkRadius, color);
}

// Header content, in absolute panel coordinates. The Core2's header
// sprite is exactly kScreenW x (kHeaderH+1) anchored at the origin, so
// the same coordinates address the sprite and the panel alike and this
// needs no separate sprite-relative path.
void drawHeaderInto(lgfx::LGFXBase& g, const char* title) {
  ofd::BatteryViewModel batt;
  ofd::buildBatteryViewModel(s_ctx != nullptr ? s_ctx->battery : ofd::BatteryState{}, batt);
  const bool wifiConnected = s_ctx != nullptr && s_ctx->wifiConnected;

  g.fillRect(0, 0, kScreenW, kHeaderH, COLOR_HEADER_BG);

  const theme::FontSpec headerFont = FONT_MICRO_HEADER();
  drawFitText(g, title, kHeaderTitleX, kHeaderTitleY, kHeaderTitleMaxRightX - kHeaderTitleX, headerFont,
              nullptr, COLOR_TEXT_PRIMARY, COLOR_HEADER_BG, textdatum_t::top_left);

  drawWifiIcon(g, kHeaderWifiIconLeftX, kHeaderIconY, wifiConnected);
  drawBatteryIcon(g, kHeaderBatteryIconLeftX, kHeaderIconY, batt);

  applyFont(g, FONT_MICRO_LABEL());
  g.setTextDatum(textdatum_t::top_right);
  g.setTextColor(colorForRole(batt.colorRole), COLOR_HEADER_BG);
  g.drawString(batt.percentText, kHeaderBatteryPercentRightX, kHeaderBatteryPercentY);

  g.drawFastHLine(0, kHeaderAccentLineY, kScreenW, COLOR_ACCENT);
}

}  // namespace

// ---- frame lifecycle ----

lgfx::LGFXBase& gfx() {
  if (g_frameCanvasReady) return g_frameCanvas;
  return M5.Display;
}

void begin() {
  board::beginDisplay();
  M5.Display.setTextWrap(false);
  setupBuffers();

  // A layout profile that disagrees with the panel it's drawing on
  // produces a screen that looks deliberate and is silently wrong, so
  // say so loudly. Checked at runtime rather than compile time because
  // the panel reports its own size only once the driver is up -- and on
  // a board whose display controller varies by production revision,
  // that's exactly the kind of thing worth hearing about on serial.
  const int panelW = M5.Display.width();
  const int panelH = M5.Display.height();
  if (panelW != kScreenW || panelH != kScreenH) {
    Serial.printf("[display] PANEL SIZE MISMATCH: %s reports %dx%d, layout profile expects %dx%d -- "
                  "the UI will be drawn to the wrong coordinates\n",
                  board::kProductName, panelW, panelH, kScreenW, kScreenH);
  }

  M5.Display.fillScreen(COLOR_BACKGROUND);
  if (g_frameCanvasReady) g_frameCanvas.fillSprite(COLOR_BACKGROUND);
}

void endFrame() {
  if (g_frameCanvasReady) g_frameCanvas.pushSprite(0, 0);
}

// ---- text ----

void drawFitText(lgfx::LGFXBase& target, const char* text, int x, int y, int maxWidth,
                 const theme::FontSpec& primary, const theme::FontSpec* fallback, uint16_t color,
                 uint16_t bg, textdatum_t datum) {
  target.setTextDatum(datum);
  target.setTextColor(color, bg);

  applyFont(target, primary);
  if (target.textWidth(text) <= maxWidth) {
    target.drawString(text, x, y);
    return;
  }

  if (fallback != nullptr) {
    applyFont(target, *fallback);
    if (target.textWidth(text) <= maxWidth) {
      target.drawString(text, x, y);
      return;
    }
  }

  // Ellipsize at whichever role is currently set.
  char buf[48];
  std::strncpy(buf, text, sizeof(buf) - 1);
  buf[sizeof(buf) - 1] = '\0';
  size_t len = std::strlen(buf);
  char withEllipsis[52];
  while (len > 0) {
    buf[len] = '\0';
    std::snprintf(withEllipsis, sizeof(withEllipsis), "%s\xE2\x80\xA6", buf);  // "…"
    if (target.textWidth(withEllipsis) <= maxWidth) {
      target.drawString(withEllipsis, x, y);
      return;
    }
    len--;
  }
  target.drawString("\xE2\x80\xA6", x, y);
}

// ---- persistent chrome ----

void drawHeader(const char* title) {
  if (g_headerSpriteReady) {
    drawHeaderInto(g_headerSprite, title);
    g_headerSprite.pushSprite(0, 0);
    return;
  }
  drawHeaderInto(gfx(), title);
}

void clearBody() {
  gfx().fillRect(0, kHeaderH + 1, kScreenW, kScreenH - kHeaderH - 1, COLOR_BACKGROUND);
}

void clearOperationalBody() {
  gfx().fillRect(0, kHeaderH + 1, kScreenW, kTabBarY - kHeaderH - 1, COLOR_BACKGROUND);
}

namespace {

const char* tabLabel(DetailPage page) {
  switch (page) {
    // The primary page relabels itself while a flight is being followed,
    // because that's what it's showing. This keeps the board at three
    // pages rather than four -- which matters on the Core2, where each
    // tab column sits above one physical button and a fourth would have
    // nowhere to live.
    case DetailPage::Flight:
      return (s_ctx != nullptr && s_ctx->trackingActive()) ? "TRACK" : "FLIGHT";
    case DetailPage::Detail: return "DETAIL";
    case DetailPage::System: return "SYSTEM";
  }
  return "FLIGHT";
}

}  // namespace

void drawTabBar() {
  auto& g = gfx();
  const DetailPage current = s_ctx != nullptr ? s_ctx->currentPage : DetailPage::Flight;

  g.fillRect(0, kTabBarY, kScreenW, kTabBarH, COLOR_HEADER_BG);
  g.drawFastHLine(0, kTabBarY, kScreenW, COLOR_GRID);
  g.drawFastVLine(kTabBarColBoundaries[1], kTabBarY, kTabBarH, COLOR_GRID);
  g.drawFastVLine(kTabBarColBoundaries[2], kTabBarY, kTabBarH, COLOR_GRID);

  const DetailPage pages[3] = {DetailPage::Flight, DetailPage::Detail, DetailPage::System};
  for (int i = 0; i < 3; i++) {
    const int colX = kTabBarColBoundaries[i];
    const int colW = kTabBarColBoundaries[i + 1] - colX;
    const bool active = pages[i] == current;

    applyFont(g, FONT_MICRO_LABEL());
    g.setTextDatum(textdatum_t::middle_center);
    g.setTextColor(active ? COLOR_ACCENT : COLOR_TEXT_SECONDARY, COLOR_HEADER_BG);
    g.drawString(tabLabel(pages[i]), colX + colW / 2, kTabBarY + kTabBarH / 2);

    if (active) {
      g.fillRect(colX + 1, kTabBarY, colW - 2, kTabBarActiveIndicatorH, COLOR_ACCENT);
    }
  }
}

// ---- composite bodies ----

void drawStatusBody(const char* title, const char* body, const char* footnote) {
  auto& g = gfx();
  applyFont(g, FONT_VALUE_LARGE());
  g.setTextDatum(textdatum_t::top_center);
  g.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  g.drawString(title, kScreenW / 2, kStatusTitleY);

  if (body != nullptr && body[0] != '\0') {
    applyFont(g, FONT_MICRO_HEADER());
    g.setTextDatum(textdatum_t::top_center);
    g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
    g.drawString(body, kScreenW / 2, kStatusBodyY);
  }

  if (footnote != nullptr && footnote[0] != '\0') {
    applyFont(g, FONT_MICRO_LABEL());
    g.setTextDatum(textdatum_t::top_center);
    g.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
    g.drawString(footnote, kScreenW / 2, kStatusFootnoteY);
  }
}

// Both label and value use the micro bitmap role at the same size --
// distinguished by color only, not size. This is a deliberate change
// from an earlier version that used a larger GFXFF font for the value:
// that font's own line height left no room before the next row started,
// and its width overflowed the value column for long content (SSIDs up
// to 32 chars, "47.6062, -122.3321"-style coordinates) -- found by
// testing on real hardware, where the value text visibly overran into
// neighboring rows. The bitmap face's fixed cell width guarantees every
// value in practice fits or ellipsizes cleanly instead of overlapping.
void drawDetailRow(int rowIndex, const char* label, const char* value, uint16_t valueColor) {
  auto& g = gfx();
  const int y = kDetailTop + rowIndex * kDetailRowH;

  applyFont(g, FONT_MICRO_LABEL());
  g.setTextDatum(textdatum_t::top_left);
  g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  g.drawString(label, kDetailLabelX, y);

  const int maxW = kScreenW - kDetailValueX - kIdentityRightMarginX;
  const theme::FontSpec micro = FONT_MICRO_LABEL();
  drawFitText(g, value, kDetailValueX, y, maxW, micro, nullptr, valueColor, COLOR_BACKGROUND,
              textdatum_t::top_left);
}

void drawIdentityBlock(const ofd::AircraftViewModel& vm) {
  auto& g = gfx();

  const uint16_t callsignColor = vm.callsignIsPlaceholder ? COLOR_TEXT_SECONDARY : COLOR_TEXT_PRIMARY;
  const theme::FontSpec identifierFallback = FONT_IDENTIFIER_FALLBACK();
  drawFitText(g, vm.callsign, kIdentityLeftX, kCallsignY,
              kIdentityBlockW - kIdentityLeftX - kIdentityRightMarginX, FONT_IDENTIFIER_PRIMARY(),
              &identifierFallback, callsignColor, COLOR_BACKGROUND, textdatum_t::top_left);

  if (vm.hasAirline) {
    drawFitText(g, vm.airlineName, kIdentityLeftX, kAirlineTypeY + kAirlineTextOffsetY, kAirlineMaxWidth,
                FONT_LABEL_REGULAR(), nullptr, COLOR_TEXT_SECONDARY, COLOR_BACKGROUND,
                textdatum_t::top_left);
  }

  g.drawRect(kTypeBadgeX, kAirlineTypeY, kTypeBadgeW, kAirlineTypeH, COLOR_GRID);
  applyFont(g, FONT_VALUE_SMALL());
  g.setTextDatum(textdatum_t::middle_center);
  g.setTextColor(COLOR_ACCENT, COLOR_BACKGROUND);
  g.drawString(vm.aircraftType, kTypeBadgeX + kTypeBadgeW / 2, kAirlineTypeY + kAirlineTypeH / 2);

  char icaoLine[16];
  std::snprintf(icaoLine, sizeof(icaoLine), "ICAO %s", vm.icao);
  applyFont(g, FONT_MICRO_LABEL());
  g.setTextDatum(textdatum_t::top_left);
  g.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  g.drawString(icaoLine, kIdentityLeftX, kIcaoY + kIcaoTextOffsetY);

  // Data-freshness caption, right-aligned on the ICAO line.
  char ageText[8];
  ofd::formatDataAge(vm.ageSeconds, vm.stale, ageText, sizeof(ageText));
  g.setTextDatum(textdatum_t::top_right);
  g.setTextColor(vm.stale ? COLOR_CAUTION : COLOR_TEXT_DIM, COLOR_BACKGROUND);
  g.drawString(ageText, kIdentityBlockW - kIdentityRightMarginX, kIcaoY + kIcaoTextOffsetY);

  g.drawFastHLine(0, kIdentityDividerY, kIdentityBlockW, COLOR_GRID);
}

void drawGridFrame() {
  auto& g = gfx();
  g.drawFastVLine(kGridColBoundaries[1], kGridTop, kGridBottom - kGridTop, COLOR_GRID);
  g.drawFastVLine(kGridColBoundaries[2], kGridTop, kGridBottom - kGridTop, COLOR_GRID);
  g.drawFastHLine(0, kGridTop + kGridRowH, kIdentityBlockW, COLOR_GRID);
}

// ---- operational grid ----

CellRect gridCell(int col, int row) {
  CellRect r;
  r.x = kGridColBoundaries[col];
  r.w = kGridColBoundaries[col + 1] - r.x;
  r.y = kGridTop + row * kGridRowH;
  r.h = kGridRowH;
  return r;
}

void drawCellLabel(const CellRect& cell, const char* label) {
  auto& g = gfx();
  applyFont(g, FONT_MICRO_LABEL());
  g.setTextDatum(textdatum_t::top_left);
  g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  g.drawString(label, cell.x + kGridCellPadX, cell.y + kGridLabelOffsetY);
}

// Value + inline unit, left-aligned, auto-fit within the cell.
void drawCellValueWithUnit(const CellRect& cell, const char* value, const char* unit, uint16_t color) {
  auto& g = gfx();
  const int maxW = cell.w - kGridCellPadX * 2;
  const int x = cell.x + kGridCellPadX;
  const int y = cell.y + kGridValueOffsetY;

  applyFont(g, FONT_VALUE_LARGE());
  g.setTextColor(color, COLOR_BACKGROUND);
  g.setTextDatum(textdatum_t::top_left);
  const int valueW = g.textWidth(value);

  if (unit != nullptr && unit[0] != '\0') {
    applyFont(g, FONT_MICRO_LABEL());
    const int unitW = g.textWidth(unit);
    if (valueW + kGridValueUnitGap + unitW <= maxW) {
      applyFont(g, FONT_VALUE_LARGE());
      g.setTextColor(color, COLOR_BACKGROUND);
      g.drawString(value, x, y);
      applyFont(g, FONT_MICRO_LABEL());
      g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
      g.drawString(unit, x + valueW + kGridValueUnitGap, y + kGridUnitBaselineOffsetY);
      return;
    }
  }

  // Unit doesn't fit inline (or there isn't one) -- value alone, with the
  // unit (if any) stacked on its own caption line beneath.
  const theme::FontSpec valueSmall = FONT_VALUE_SMALL();
  drawFitText(g, value, x, y, maxW, FONT_VALUE_LARGE(), &valueSmall, color, COLOR_BACKGROUND,
              textdatum_t::top_left);
  if (unit != nullptr && unit[0] != '\0') {
    applyFont(g, FONT_MICRO_LABEL());
    g.setTextDatum(textdatum_t::top_left);
    g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
    g.drawString(unit, x, cell.y + kGridCaptionOffsetY);
  }
}

void drawTrackCell(const CellRect& cell, const ofd::AircraftViewModel& vm) {
  auto& g = gfx();
  const int x = cell.x + kGridCellPadX;
  const int y = cell.y + kGridValueOffsetY;

  if (!vm.hasTrack) {
    drawFitText(g, kPlaceholderDash, x, y, cell.w - kGridCellPadX * 2, FONT_VALUE_LARGE(), nullptr,
                COLOR_TEXT_PRIMARY, COLOR_BACKGROUND, textdatum_t::top_left);
    return;
  }

  applyFont(g, FONT_VALUE_LARGE());
  g.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  g.setTextDatum(textdatum_t::top_left);
  g.drawString(vm.trackDegrees, x, y);
  drawDegreeMark(g, x + g.textWidth(vm.trackDegrees) + kDegreeMarkGap, y, COLOR_TEXT_PRIMARY);

  applyFont(g, FONT_MICRO_LABEL());
  g.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  g.drawString(vm.trackCompass, x, cell.y + kGridCaptionOffsetY);
}

void drawStatusCell(const CellRect& cell, const ofd::AircraftViewModel& vm) {
  auto& g = gfx();
  const uint16_t color = colorForRole(ofd::displayStatusColorRole(vm.status));
  const char* word = ofd::displayStatusWord(vm.status);

  // Colored left accent bar instead of a filled badge/card -- a clean
  // separator-based treatment per docs/DISPLAY_UI.md rather than a
  // decorative panel.
  g.fillRect(cell.x, cell.y + kGridLabelOffsetY, kStatusAccentBarW,
             cell.h - kGridLabelOffsetY - kStatusAccentBarBottomInset, color);

  const int x = cell.x + kGridCellPadX + kStatusAccentTextGap;
  const int maxW = cell.w - kGridCellPadX * 2 - kStatusAccentTextGap;
  const theme::FontSpec valueSmall = FONT_VALUE_SMALL();
  drawFitText(g, word, x, cell.y + kGridValueOffsetY, maxW, FONT_VALUE_LARGE(), &valueSmall, color,
              COLOR_BACKGROUND, textdatum_t::top_left);
}

void drawMetricGrid(const ofd::AircraftViewModel& vm) {
  drawGridFrame();

  drawCellLabel(gridCell(0, 0), "DIST");
  drawCellValueWithUnit(gridCell(0, 0), vm.hasDistance ? vm.distanceValue : kPlaceholderDash,
                        vm.hasDistance ? ofd::AircraftViewModel::kDistanceUnit : "", COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(1, 0), "ALT");
  drawCellValueWithUnit(gridCell(1, 0), vm.hasAltitude ? vm.altitudeValue : kPlaceholderDash,
                        (vm.hasAltitude && !vm.altitudeIsGround) ? ofd::AircraftViewModel::kAltitudeUnit : "",
                        COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(2, 0), "SPEED");
  drawCellValueWithUnit(gridCell(2, 0), vm.hasSpeed ? vm.speedValue : kPlaceholderDash,
                        vm.hasSpeed ? ofd::AircraftViewModel::kSpeedUnit : "", COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(0, 1), "TRACK");
  drawTrackCell(gridCell(0, 1), vm);

  drawCellLabel(gridCell(1, 1), "V/S");
  drawCellValueWithUnit(gridCell(1, 1), vm.hasVerticalRate ? vm.verticalRateValue : kPlaceholderDash,
                        vm.hasVerticalRate ? ofd::AircraftViewModel::kVerticalRateUnit : "",
                        COLOR_TEXT_PRIMARY);

  drawCellLabel(gridCell(2, 1), "STATUS");
  drawStatusCell(gridCell(2, 1), vm);
}

}  // namespace draw

// ---- public interface ----
//
// Display::renderAircraft lives in display_flight_<board>.cpp -- it is
// the one screen whose structure, not just its proportions, differs
// between panels.

using namespace ofd::app::theme;
using namespace ofd::app::layout;
using draw::kPlaceholderDash;
using lgfx::textdatum_t;

void Display::begin() { draw::begin(); }

void Display::renderBoot(const char* firmwareVersion) {
  draw::clearBody();
  draw::drawHeader("OPEN FLIGHT DISPLAY");

  auto& gfx = draw::gfx();
  applyFont(gfx, FONT_IDENTIFIER_FALLBACK());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.drawString("OPENFLIGHT", kScreenW / 2, kStatusTitleY);

  applyFont(gfx, FONT_MICRO_HEADER());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("INITIALIZING", kScreenW / 2, kStatusBodyY);

  char version[24];
  std::snprintf(version, sizeof(version), "FIRMWARE %s", firmwareVersion);
  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString(version, kScreenW / 2, kStatusFootnoteY);

  draw::endFrame();
}

void Display::renderProvisioning(const char* apName) {
  draw::clearBody();
  draw::drawHeader("WI-FI SETUP");

  auto& gfx = draw::gfx();
  applyFont(gfx, FONT_MICRO_HEADER());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("CONNECT YOUR PHONE TO:", kScreenW / 2, kProvisionPromptY);

  gfx.drawRoundRect(kStatusMargin, kProvisionBoxY, kScreenW - kStatusMargin * 2, kProvisionBoxH,
                    kProvisionBoxRadius, COLOR_GRID);
  const theme::FontSpec valueSmall = FONT_VALUE_SMALL();
  draw::drawFitText(gfx, apName, kScreenW / 2, kProvisionApNameY,
                    kScreenW - kStatusMargin * 2 - kProvisionBoxTextInset, FONT_VALUE_LARGE(),
                    &valueSmall, COLOR_TEXT_PRIMARY, COLOR_BACKGROUND, textdatum_t::top_center);

  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString("THEN OPEN 192.168.4.1", kScreenW / 2, kStatusFootnoteY);

  draw::endFrame();
}

void Display::renderLocationRequired(const char* ipAddress, const char* pairingCode) {
  draw::clearBody();
  draw::drawHeader("SETUP REQUIRED");

  auto& gfx = draw::gfx();

  char url[64];
  std::snprintf(url, sizeof(url), "http://%s/pair?code=%s", ipAddress, pairingCode);

  QRCode qrcode;
  uint8_t qrBuf[qrcode_getBufferSize(6)];
  qrcode_initText(&qrcode, qrBuf, 6, ECC_MEDIUM, url);
  const int qrPx = qrcode.size * kQrModuleSize;
  const int qrY = kHeaderH + (kScreenH - kHeaderH - qrPx) / 2;
  gfx.fillRect(kQrX - kQrQuietZone, qrY - kQrQuietZone, qrPx + kQrQuietZone * 2,
               qrPx + kQrQuietZone * 2, COLOR_TEXT_PRIMARY);
  for (uint8_t y = 0; y < qrcode.size; y++) {
    for (uint8_t x = 0; x < qrcode.size; x++) {
      if (qrcode_getModule(&qrcode, x, y)) {
        gfx.fillRect(kQrX + x * kQrModuleSize, qrY + y * kQrModuleSize, kQrModuleSize, kQrModuleSize,
                     COLOR_BACKGROUND);
      }
    }
  }

  const int textX = kQrX + qrPx + kSetupTextGapX;
  const int maxW = kScreenW - textX - kIdentityRightMarginX;

  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("BROWSE TO", textX, kSetupLabelY);

  const theme::FontSpec valueSmall = FONT_VALUE_SMALL();
  draw::drawFitText(gfx, ipAddress, textX, kSetupIpY, maxW, FONT_VALUE_LARGE(), &valueSmall,
                    COLOR_TEXT_PRIMARY, COLOR_BACKGROUND, textdatum_t::top_left);

  applyFont(gfx, FONT_VALUE_SMALL());
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("/setup", textX, kSetupPathY);

  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.drawString("SCAN QR OR TYPE MANUALLY", textX, kSetupHintY);

  draw::endFrame();
}

void Display::renderTrackedFlight() {
  if (s_ctx == nullptr) return;
  const ofd::TrackedFlightConfig& tracked = s_ctx->config.trackedFlight;
  const ofd::FlightProgress& progress = s_ctx->trackedProgress;

  draw::clearOperationalBody();

  char title[32];
  std::snprintf(title, sizeof(title), "TRACKING %s", tracked.label);
  draw::drawHeader(title);

  auto& gfx = draw::gfx();

  // Nothing has been heard from this flight yet. That is the normal
  // state before pushback -- and also what a mistyped flight number
  // looks like -- so the screen says which flight it's waiting for and
  // for how long, rather than showing a spinner or inventing an ETA.
  if (progress.phase == ofd::FlightPhase::AwaitingContact) {
    char body[48];
    if (s_ctx->trackedDestinationUnresolved) {
      std::snprintf(body, sizeof(body), "UNKNOWN AIRPORT %s", tracked.destinationIcao);
      draw::drawStatusBody("CHECK DESTINATION", body, "TRACKING CANNOT START");
    } else {
      std::snprintf(body, sizeof(body), "WAITING FOR %s", tracked.label);
      draw::drawStatusBody(body, "NOT YET TRANSMITTING", "NORMAL BEFORE DEPARTURE");
    }
    draw::drawSecondaryColumn(0, false);
    draw::drawTabBar();
    draw::endFrame();
    return;
  }

  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(s_ctx->trackedAircraft, progress.secondsSinceContact,
                              progress.phase == ofd::FlightPhase::LostContact, vm);
  draw::drawIdentityBlock(vm);
  draw::drawGridFrame();

  // The six cells answer, in reading order, the only questions that
  // matter when you're deciding whether to get in the car: when, how
  // far, where, how high, how fast, what's it doing.
  char eta[8];
  ofd::formatMinutesRemaining(progress.hasEta, progress.minutesRemaining, eta, sizeof(eta));
  draw::drawCellLabel(draw::gridCell(0, 0), "ARRIVES IN");
  draw::drawCellValueWithUnit(draw::gridCell(0, 0), eta, progress.hasEta ? "MIN" : "",
                              progress.hasEta ? COLOR_GOOD : COLOR_TEXT_SECONDARY);

  char togo[12];
  if (progress.hasDistance) {
    std::snprintf(togo, sizeof(togo), "%.0f", progress.distanceToDestinationKm / 1.852);
  } else {
    std::strcpy(togo, kPlaceholderDash);
  }
  draw::drawCellLabel(draw::gridCell(1, 0), "TO GO");
  draw::drawCellValueWithUnit(draw::gridCell(1, 0), togo, progress.hasDistance ? "NM" : "",
                              COLOR_TEXT_PRIMARY);

  draw::drawCellLabel(draw::gridCell(2, 0), "DEST");
  draw::drawCellValueWithUnit(draw::gridCell(2, 0),
                              s_ctx->trackedDestination.valid ? s_ctx->trackedDestination.icao
                                                              : tracked.destinationIcao,
                              "", COLOR_ACCENT);

  draw::drawCellLabel(draw::gridCell(0, 1), "ALT");
  draw::drawCellValueWithUnit(draw::gridCell(0, 1), vm.hasAltitude ? vm.altitudeValue : kPlaceholderDash,
                              (vm.hasAltitude && !vm.altitudeIsGround) ? ofd::AircraftViewModel::kAltitudeUnit : "",
                              COLOR_TEXT_PRIMARY);

  draw::drawCellLabel(draw::gridCell(1, 1), "SPEED");
  draw::drawCellValueWithUnit(draw::gridCell(1, 1), vm.hasSpeed ? vm.speedValue : kPlaceholderDash,
                              vm.hasSpeed ? ofd::AircraftViewModel::kSpeedUnit : "", COLOR_TEXT_PRIMARY);

  // Phase gets the same colored accent-bar treatment as the STATUS cell
  // on the nearest-aircraft screen, so the two pages read alike.
  const ofd::FlightPhase phase = progress.phase;
  const uint16_t phaseColor = (phase == ofd::FlightPhase::LostContact)  ? COLOR_CAUTION
                              : (phase == ofd::FlightPhase::Approaching) ? COLOR_GOOD
                                                                         : COLOR_TEXT_PRIMARY;
  const draw::CellRect phaseCell = draw::gridCell(2, 1);
  gfx.fillRect(phaseCell.x, phaseCell.y + kGridLabelOffsetY, kStatusAccentBarW,
               phaseCell.h - kGridLabelOffsetY - kStatusAccentBarBottomInset, phaseColor);
  draw::drawCellLabel(phaseCell, "STATUS");
  const theme::FontSpec phaseFallback = FONT_VALUE_SMALL();
  draw::drawFitText(gfx, ofd::flightPhaseWord(phase), phaseCell.x + kGridCellPadX + kStatusAccentTextGap,
                    phaseCell.y + kGridValueOffsetY,
                    phaseCell.w - kGridCellPadX * 2 - kStatusAccentTextGap, FONT_VALUE_LARGE(),
                    &phaseFallback, phaseColor, COLOR_BACKGROUND, textdatum_t::top_left);

  draw::drawSecondaryColumn(progress.secondsSinceContact,
                            phase == ofd::FlightPhase::LostContact);
  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderSearching() {
  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawStatusBody("SEARCHING FOR AIRCRAFT", "CONNECTING TO ADS-B DATA SOURCE", nullptr);
  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderNoTraffic(bool hasClock, const char* timeHhMm) {
  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawStatusBody("NO NEARBY AIRCRAFT", "MONITORING WITHIN CONFIGURED RADIUS", nullptr);

  if (hasClock && timeHhMm != nullptr && timeHhMm[0] != '\0') {
    auto& gfx = draw::gfx();
    applyFont(gfx, FONT_VALUE_LARGE());
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
    gfx.drawString(timeHhMm, kScreenW / 2, kNoTrafficClockY);
  }

  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderWifiOffline() {
  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawStatusBody("WI-FI OFFLINE", "ATTEMPTING TO RECONNECT", nullptr);
  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderApiError() {
  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawStatusBody("ADS-B DATA UNAVAILABLE", "DATA SOURCE UNREACHABLE", nullptr);
  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderAircraftDetail(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  draw::clearOperationalBody();
  draw::drawHeader("FLIGHT DETAIL");

  char ageText[8];
  ofd::formatDataAge(vm.ageSeconds, vm.stale, ageText, sizeof(ageText));
  const uint16_t ageColor = vm.stale ? COLOR_CAUTION : COLOR_TEXT_PRIMARY;

  // No literal degree sign here (the GFXFF value font only covers ASCII
  // 0x20-0x7E -- same reason drawTrackCell draws a circle primitive
  // instead of a glyph on the FLIGHT page). "247 WSW" reads unambiguously
  // without it in a single-line label/value row.
  char bearingText[16];
  if (vm.hasBearing) {
    std::snprintf(bearingText, sizeof(bearingText), "%s %s", vm.bearingDegrees, vm.bearingCompass);
  } else {
    std::strcpy(bearingText, kPlaceholderDash);
  }

  int row = 0;
  draw::drawDetailRow(row++, "CALLSIGN", vm.callsign, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "TYPE", vm.aircraftType, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "ICAO", vm.icao, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "SQUAWK", vm.squawk, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "POSITION", vm.position, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "BEARING", bearingText, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "DATA AGE", ageText, ageColor);

  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderDetailPlaceholder() {
  draw::clearOperationalBody();
  draw::drawHeader("FLIGHT DETAIL");
  draw::drawStatusBody("NO AIRCRAFT DATA", "SWITCH TO THE FLIGHT TAB FOR STATUS", nullptr);
  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderSystemInfo() {
  draw::clearOperationalBody();
  draw::drawHeader("SYSTEM");

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
    std::strcpy(batteryText, kPlaceholderDash);
  }

  const uint32_t upSeconds = millis() / 1000;
  char uptimeText[16];
  std::snprintf(uptimeText, sizeof(uptimeText), "%02u:%02u:%02u", static_cast<unsigned>(upSeconds / 3600),
                static_cast<unsigned>((upSeconds / 60) % 60), static_cast<unsigned>(upSeconds % 60));

  char ipText[20];
  std::strncpy(ipText, wifiConnected ? WiFi.localIP().toString().c_str() : kPlaceholderDash,
               sizeof(ipText) - 1);
  ipText[sizeof(ipText) - 1] = '\0';

  // Board identity rides along on the FIRMWARE row rather than claiming
  // a row of its own: the Core2's list is already 8 rows deep against a
  // 9-row budget before the tab bar, and "which board am I looking at"
  // is a question you ask once, not a metric you watch.
  char firmwareText[40];
  std::snprintf(firmwareText, sizeof(firmwareText), "%s (%s)",
                s_ctx != nullptr ? s_ctx->firmwareVersion : "?", board::kProductName);

  int row = 0;
  draw::drawDetailRow(row++, "WI-FI", wifiText, wifiConnected ? COLOR_TEXT_PRIMARY : COLOR_CRITICAL);
  draw::drawDetailRow(row++, "IP ADDRESS", ipText, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "DATA SOURCE", "ADS-B (ADSB.LOL)", COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "PROVIDER", providerText, providerColor);
  draw::drawDetailRow(row++, "BATTERY", batteryText, colorForRole(batt.colorRole));
  draw::drawDetailRow(row++, "DEVICE ID", s_ctx != nullptr ? s_ctx->deviceId : kPlaceholderDash,
                      COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "FIRMWARE", firmwareText, COLOR_TEXT_PRIMARY);
  draw::drawDetailRow(row++, "UPTIME", uptimeText, COLOR_TEXT_PRIMARY);

  draw::drawTabBar();
  draw::endFrame();
}

void Display::renderOtaProgress(uint8_t percent, bool complete, const char* status) {
  draw::clearBody();
  draw::drawHeader("FIRMWARE UPDATE");

  auto& gfx = draw::gfx();
  applyFont(gfx, FONT_VALUE_LARGE());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
  gfx.drawString(complete ? "UPDATE COMPLETE" : "UPDATING FIRMWARE", kScreenW / 2, kOtaTitleY);

  applyFont(gfx, FONT_MICRO_HEADER());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(status, kScreenW / 2, kOtaStatusY);

  if (!complete) {
    const int bx = kStatusMargin;
    const int bw = kScreenW - kStatusMargin * 2;
    gfx.drawRoundRect(bx, kOtaBarY, bw, kOtaBarH, kOtaBarRadius, COLOR_GRID);
    if (percent > 0) {
      const int fillW = (bw - kOtaBarInset * 2) * percent / 100;
      gfx.fillRoundRect(bx + kOtaBarInset, kOtaBarY + kOtaBarInset, fillW, kOtaBarH - kOtaBarInset * 2,
                        kOtaBarRadius / 2, COLOR_ACCENT);
    }
    char pct[8];
    std::snprintf(pct, sizeof(pct), "%u%%", percent);
    applyFont(gfx, FONT_VALUE_LARGE());
    gfx.setTextDatum(textdatum_t::top_center);
    gfx.setTextColor(COLOR_TEXT_PRIMARY, COLOR_BACKGROUND);
    gfx.drawString(pct, kScreenW / 2, kOtaBarY + kOtaBarH + kOtaPercentGapY);
  }

  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextDatum(textdatum_t::top_center);
  gfx.setTextColor(COLOR_CRITICAL, COLOR_BACKGROUND);
  gfx.drawString(complete ? "RESTARTING..." : "DO NOT REMOVE POWER", kScreenW / 2, kStatusFootnoteY);

  draw::endFrame();
}

void Display::update() { M5.update(); }

}  // namespace ofd::app
