#pragma once

#include "domain/aircraft.h"
#include "domain/monitoring_area.h"

namespace ofd {

// Mirrors services/gateway/src/lib/ranking.ts's rankNearest: filters to
// aircraft within the circle, (re)computes distance/bearing from the
// area's center, and returns them sorted nearest-first, bounded to
// maxResults. The gateway already does this before sending, but the
// Core2 repeats it locally so it can keep showing a sane result if it
// ever receives an unsorted or slightly-stale list, and so the logic is
// unit-testable without a network (docs/ARCHITECTURE.md).
AircraftList rankNearest(const AircraftList& input, const CircleMonitoringArea& area,
                          size_t maxResults = kMaxAircraftPerUpdate);

}  // namespace ofd
