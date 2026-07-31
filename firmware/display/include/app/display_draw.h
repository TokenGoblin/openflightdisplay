#pragma once

#include <M5Unified.h>

#include "app/ui_theme.h"
#include "domain/aircraft.h"
#include "domain/display_format.h"

// Shared drawing primitives for the FIDS renderer.
//
// This is an internal header: the only consumers are src/app/display.cpp
// (which implements every structurally-shared screen state) and the
// per-board FLIGHT-page renderers in src/app/display_flight_*.cpp. The
// FLIGHT page is the one screen whose *structure* genuinely differs
// between boards -- a single aircraft filling a 320x240 panel versus a
// hero column plus a traffic board on a 1280x720 one -- so it is the one
// screen that gets a file per board. Everything else differs only in the
// numbers, which is what the layout profile is for.
//
// Nothing outside src/app/ should include this; app/display.h is the
// public interface.

namespace ofd::app::draw {

using lgfx::textdatum_t;

// The em dash the renderer shows in place of a value the provider didn't
// supply. UTF-8, as a named constant so the escape isn't retyped at
// twenty call sites.
constexpr const char* kPlaceholderDash = "\xE2\x80\x94";

// ---- frame lifecycle ----

// The drawing target for the current frame. On a board with PSRAM to
// spare this is an off-screen full-panel canvas; otherwise it is the
// panel itself. Callers draw the same way either way.
lgfx::LGFXBase& gfx();

// One-time setup: panel orientation via board::beginDisplay(), plus
// whichever buffering strategy the board uses. Called by Display::begin().
void begin();

// Every render* method in the public Display interface must end with
// endFrame(): on a canvas-buffered board that is what actually makes the
// frame visible. It is a no-op on a direct-to-panel board, so forgetting
// it produces a screen that works on one board and stays frozen on the
// other.
void endFrame();

// ---- text ----

// Draws `text` fitted to `maxWidth`: at `primary` if it fits, else at
// `fallback` (may be null to skip straight to ellipsizing), else
// ellipsized at whichever role is current. Every piece of dynamic text
// goes through here and is measured with the font's own textWidth(),
// never estimated from a character count.
//
// Takes its target explicitly rather than using gfx(), because the
// header is drawn into a separate sprite on some boards.
void drawFitText(lgfx::LGFXBase& target, const char* text, int x, int y, int maxWidth,
                 const theme::FontSpec& primary, const theme::FontSpec* fallback, uint16_t color,
                 uint16_t bg, textdatum_t datum);

// ---- persistent chrome ----

// Title on the left, live Wi-Fi/battery status on the right, read
// straight from the ambient AppContext so no caller has to pass it.
void drawHeader(const char* title);

// Full height below the header -- for the pre-operational screens
// (boot/provisioning/setup-required/OTA) that have no tab bar.
void clearBody();

// Down to the top of the tab bar -- for every operational-state screen.
void clearOperationalBody();

// FLIGHT / DETAIL / SYSTEM. Reads the active page from the ambient
// AppContext. Present on every operational-state screen so navigation
// works identically no matter what is on screen.
void drawTabBar();

// ---- composite bodies ----

void drawStatusBody(const char* title, const char* body, const char* footnote);
void drawDetailRow(int rowIndex, const char* label, const char* value, uint16_t valueColor);
void drawIdentityBlock(const ofd::AircraftViewModel& vm);
void drawGridFrame();

// ---- operational grid ----

struct CellRect {
  int x, y, w, h;
};

CellRect gridCell(int col, int row);
void drawCellLabel(const CellRect& cell, const char* label);
void drawCellValueWithUnit(const CellRect& cell, const char* value, const char* unit, uint16_t color);
void drawTrackCell(const CellRect& cell, const ofd::AircraftViewModel& vm);
void drawStatusCell(const CellRect& cell, const ofd::AircraftViewModel& vm);

// Fills the six cells of the operational grid from `vm`. Both boards
// show the same six metrics in the same positions -- the Tab5 just does
// it inside its hero column -- so the cell-by-cell content lives here
// rather than being retyped in each FLIGHT renderer.
void drawMetricGrid(const ofd::AircraftViewModel& vm);

// The board's secondary content area, drawn by whichever screen fills the
// primary column. Implemented per board in display_flight_<board>.cpp:
// a no-op where the panel has no room for one (Core2), the
// nearby-traffic board where it does (Tab5).
//
// It's a hook rather than a call inside the Tab5 FLIGHT renderer because
// more than one primary screen needs it -- the tracked-flight page would
// otherwise leave a 500px column blank on that panel. `ageSeconds` and
// `stale` describe the freshness of the underlying aircraft list.
void drawSecondaryColumn(uint32_t ageSeconds, bool stale);

}  // namespace ofd::app::draw
