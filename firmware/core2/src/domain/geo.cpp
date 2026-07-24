#include "domain/geo.h"

#include <cmath>

namespace ofd {

namespace {
constexpr double kEarthRadiusKm = 6371.0088;

double toRadians(double deg) { return deg * M_PI / 180.0; }
double toDegrees(double rad) { return rad * 180.0 / M_PI; }
}  // namespace

double haversineDistanceKm(double lat1, double lon1, double lat2, double lon2) {
  const double dLat = toRadians(lat2 - lat1);
  const double dLon = toRadians(lon2 - lon1);
  const double a = std::sin(dLat / 2) * std::sin(dLat / 2) +
                    std::cos(toRadians(lat1)) * std::cos(toRadians(lat2)) *
                        std::sin(dLon / 2) * std::sin(dLon / 2);
  const double c = 2 * std::atan2(std::sqrt(a), std::sqrt(1 - a));
  return kEarthRadiusKm * c;
}

double initialBearingDeg(double lat1, double lon1, double lat2, double lon2) {
  const double phi1 = toRadians(lat1);
  const double phi2 = toRadians(lat2);
  const double dLon = toRadians(lon2 - lon1);
  const double y = std::sin(dLon) * std::cos(phi2);
  const double x = std::cos(phi1) * std::sin(phi2) - std::sin(phi1) * std::cos(phi2) * std::cos(dLon);
  const double bearing = toDegrees(std::atan2(y, x));
  double normalized = std::fmod(bearing + 360.0, 360.0);
  if (normalized < 0) normalized += 360.0;
  return normalized;
}

bool isWithinCircle(double lat, double lon, double centerLat, double centerLon, double radiusKm) {
  return haversineDistanceKm(lat, lon, centerLat, centerLon) <= radiusKm;
}

}  // namespace ofd
