#include <M5Unified.h>

#include <cstdio>

#include "app/display.h"
#include "app/display_draw.h"
#include "app/ui_layout.h"
#include "app/ui_theme.h"
#include "domain/display_format.h"

// The Tab5's FLIGHT page: a hero column plus a nearby-traffic board.
//
// The hero column is the Core2's screen, re-proportioned -- same identity
// block over the same six-cell metric grid, in the same reading order, so
// somebody who knows one display knows the other. The right-hand column
// is what the extra panel area buys: a departures-board list of the other
// aircraft currently inside the configured radius.
//
// That list costs nothing to produce. AdsbProvider already fetches every
// aircraft in the radius and rankNearest() already sorts them by
// distance; the Core2 simply throws away everything past items[0]
// because it has nowhere to put it. This renderer reads the same
// AircraftList straight off the ambient AppContext -- the pattern the
// header (battery/Wi-Fi) and the tab bar (current page) already use --
// which is why the Display::renderAircraft signature still takes only
// the nearest aircraft and needs no board-specific variant.

namespace ofd::app {

namespace {

using namespace ofd::app::theme;
using namespace ofd::app::layout;
using lgfx::textdatum_t;

// Every aircraft in a given update shares one poll timestamp, so the
// nearest aircraft's freshness is the whole list's freshness -- the
// caller's ageSeconds/stale apply verbatim to every row.
void drawTrafficRow(int rowIndex, const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  auto& gfx = draw::gfx();
  const int y = kTrafficRowTop + rowIndex * kTrafficRowH + kTrafficRowTextOffsetY;

  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  // An emergency squawk several miles out still matters, so the row
  // carries the same color coding the hero's STATUS cell would give it.
  const ofd::StatusColorRole role = ofd::displayStatusColorRole(vm.status);
  const uint16_t callsignColor = role == ofd::StatusColorRole::Critical
                                     ? COLOR_CRITICAL
                                     : (vm.callsignIsPlaceholder ? COLOR_TEXT_DIM : COLOR_TEXT_PRIMARY);

  const theme::FontSpec micro = FONT_MICRO_LABEL();
  draw::drawFitText(gfx, vm.callsign, kTrafficCallsignX, y,
                    kTrafficDistRightX - kTrafficCallsignX - kTrafficPadX, micro, nullptr, callsignColor,
                    COLOR_BACKGROUND, textdatum_t::top_left);

  applyFont(gfx, micro);
  gfx.setTextDatum(textdatum_t::top_right);

  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(vm.hasDistance ? vm.distanceValue : draw::kPlaceholderDash, kTrafficDistRightX, y);

  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString(vm.hasAltitude ? vm.altitudeValue : draw::kPlaceholderDash, kTrafficAltRightX, y);
}

// The other aircraft inside the radius, nearest first, skipping items[0]
// (already the hero). Silent when there aren't any -- one aircraft
// overhead and an empty list is a completely normal state, not a fault,
// and the hero column is already saying everything there is to say.
void drawTrafficBoard(uint32_t ageSeconds, bool stale) {
  auto& gfx = draw::gfx();

  gfx.drawFastVLine(kTrafficColX, kHeaderH + 1, kTabBarY - kHeaderH - 1, COLOR_GRID);

  applyFont(gfx, FONT_MICRO_LABEL());
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.setTextColor(COLOR_TEXT_SECONDARY, COLOR_BACKGROUND);
  gfx.drawString("NEARBY TRAFFIC", kTrafficCallsignX, kTrafficHeaderY);

  gfx.drawFastHLine(kTrafficColX, kTrafficDividerY, kTrafficColW, COLOR_GRID);

  gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
  gfx.setTextDatum(textdatum_t::top_left);
  gfx.drawString("FLIGHT", kTrafficCallsignX, kTrafficCaptionY);
  gfx.setTextDatum(textdatum_t::top_right);
  gfx.drawString(ofd::AircraftViewModel::kDistanceUnit, kTrafficDistRightX, kTrafficCaptionY);
  gfx.drawString(ofd::AircraftViewModel::kAltitudeUnit, kTrafficAltRightX, kTrafficCaptionY);

  const size_t count = s_ctx != nullptr ? s_ctx->latestAircraft.count : 0;
  if (count <= 1) {
    applyFont(gfx, FONT_MICRO_LABEL());
    gfx.setTextDatum(textdatum_t::top_left);
    gfx.setTextColor(COLOR_TEXT_DIM, COLOR_BACKGROUND);
    gfx.drawString("NO OTHER AIRCRAFT", kTrafficCallsignX, kTrafficEmptyY);
    return;
  }

  int rowIndex = 0;
  for (size_t i = 1; i < count && rowIndex < kTrafficMaxRows; i++, rowIndex++) {
    drawTrafficRow(rowIndex, s_ctx->latestAircraft.items[i], ageSeconds, stale);
  }
}

}  // namespace

void Display::renderAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawIdentityBlock(vm);
  draw::drawMetricGrid(vm);
  drawTrafficBoard(ageSeconds, stale);
  draw::drawTabBar();
  draw::endFrame();
}

}  // namespace ofd::app
