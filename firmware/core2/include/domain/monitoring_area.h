#pragma once

namespace ofd {

// Phase 1 only implements the circle shape end-to-end on the Core2 (see
// docs/ARCHITECTURE.md). Cone/polygon are modeled in packages/shared-models
// for later phases but deliberately have no firmware representation yet --
// a config write specifying one of those kinds is rejected by
// domain/config.cpp with a clear "not yet supported" error rather than
// silently misinterpreted.
struct CircleMonitoringArea {
  double centerLat = 0.0;
  double centerLon = 0.0;
  double radiusKm = 0.0;

  bool hasMinAltitudeFt = false;
  double minAltitudeFt = 0.0;

  bool hasMaxAltitudeFt = false;
  double maxAltitudeFt = 0.0;
};

}  // namespace ofd
