#pragma once

namespace ofd {

// Mirrors services/gateway/src/lib/geo.ts exactly so the Core2's own
// ranking/staleness logic (used when it can't reach the gateway momentarily)
// agrees with the gateway's calculations. Kept dependency-free (no Arduino
// headers) so it compiles and is testable under PlatformIO's `native` env
// without any hardware.

// Great-circle distance between two points, in kilometers.
double haversineDistanceKm(double lat1, double lon1, double lat2, double lon2);

// Initial bearing from point 1 to point 2, in degrees [0, 360).
double initialBearingDeg(double lat1, double lon1, double lat2, double lon2);

// True if (lat, lon) is within radiusKm of (centerLat, centerLon).
bool isWithinCircle(double lat, double lon, double centerLat, double centerLon, double radiusKm);

}  // namespace ofd
