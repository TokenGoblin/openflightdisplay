#include "domain/ranking.h"

#include <algorithm>
#include <cstring>

#include "domain/geo.h"

namespace ofd {

AircraftList rankNearest(const AircraftList& input, const CircleMonitoringArea& area, size_t maxResults) {
  AircraftList result;

  // Stage into a temporary buffer with distance/bearing filled in, then
  // sort, then copy the bounded head into the output -- avoids any
  // dynamic allocation.
  AircraftState staged[kMaxAircraftPerUpdate];
  size_t stagedCount = 0;

  for (size_t i = 0; i < input.count && stagedCount < kMaxAircraftPerUpdate; i++) {
    const AircraftState& a = input.items[i];
    if (!isWithinCircle(a.latitude, a.longitude, area.centerLat, area.centerLon, area.radiusKm)) {
      continue;
    }
    AircraftState enriched = a;
    enriched.distanceFromObserverKm =
        haversineDistanceKm(area.centerLat, area.centerLon, a.latitude, a.longitude);
    enriched.hasDistanceFromObserverKm = true;
    enriched.bearingFromObserverDeg =
        initialBearingDeg(area.centerLat, area.centerLon, a.latitude, a.longitude);
    enriched.hasBearingFromObserverDeg = true;
    staged[stagedCount++] = enriched;
  }

  std::sort(staged, staged + stagedCount, [](const AircraftState& a, const AircraftState& b) {
    return a.distanceFromObserverKm < b.distanceFromObserverKm;
  });

  const size_t bound = std::min(maxResults, stagedCount);
  for (size_t i = 0; i < bound; i++) {
    result.items[i] = staged[i];
  }
  result.count = bound;
  return result;
}

}  // namespace ofd
