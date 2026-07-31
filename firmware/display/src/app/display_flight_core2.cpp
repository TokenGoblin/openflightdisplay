#include "app/display.h"
#include "app/display_draw.h"
#include "app/ui_layout.h"
#include "domain/display_format.h"

// The Core2's FLIGHT page: one aircraft, filling a 320x240 panel.
//
// This board can show exactly one aircraft's worth of information, so it
// shows the nearest one -- identity block over a six-cell metric grid.
// The provider ranks and returns up to kMaxAircraftPerUpdate aircraft and
// everything past items[0] is deliberately unused here; there is nowhere
// on this panel to put it that wouldn't make the one thing that matters
// harder to read across a room. The Tab5 renderer (display_flight_tab5.cpp)
// is where the rest of that list gets shown.

namespace ofd::app {

// No secondary column on a 320x240 panel -- the primary content already
// fills it. See app/display_draw.h.
namespace draw {
void drawSecondaryColumn(uint32_t, bool) {}
}  // namespace draw

void Display::renderAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale) {
  ofd::AircraftViewModel vm;
  ofd::buildAircraftViewModel(aircraft, ageSeconds, stale, vm);

  draw::clearOperationalBody();
  draw::drawHeader("NEAREST AIRCRAFT");
  draw::drawIdentityBlock(vm);
  draw::drawMetricGrid(vm);
  draw::drawTabBar();
  draw::endFrame();
}

}  // namespace ofd::app
