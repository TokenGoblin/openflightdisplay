import type { AircraftState, CircleMonitoringArea } from "@openflightdisplay/shared-models";
import { MAX_AIRCRAFT_PER_UPDATE } from "@openflightdisplay/shared-models";
import { haversineDistanceKm, initialBearingDeg, isWithinCircle } from "./geo.js";

/** Aircraft older than this are flagged stale rather than presented as live. */
export const STALE_POSITION_THRESHOLD_MS = 60_000;

export function isStale(aircraft: Pick<AircraftState, "positionTimestamp">, now: Date): boolean {
  return now.getTime() - Date.parse(aircraft.positionTimestamp) > STALE_POSITION_THRESHOLD_MS;
}

/**
 * The only ranking mode implemented in Phase 1: nearest horizontal
 * distance to the monitoring area's center, restricted to aircraft
 * actually inside the circle. Other modes (slant range, closest
 * approach, highest/lowest/fastest, weighted relevance, ...) are
 * documented in docs/FEATURE_PARITY_MATRIX.md and deliberately not
 * implemented yet.
 */
export function rankNearest(
  aircraftList: readonly AircraftState[],
  area: CircleMonitoringArea,
  maxResults: number = MAX_AIRCRAFT_PER_UPDATE,
): AircraftState[] {
  const enriched = aircraftList
    .filter((a) => isWithinCircle(a.latitude, a.longitude, area.centerLat, area.centerLon, area.radiusKm))
    .map((a) => {
      const distanceFromObserverKm = haversineDistanceKm(
        area.centerLat,
        area.centerLon,
        a.latitude,
        a.longitude,
      );
      const bearingFromObserverDeg = initialBearingDeg(
        area.centerLat,
        area.centerLon,
        a.latitude,
        a.longitude,
      );
      return { ...a, distanceFromObserverKm, bearingFromObserverDeg };
    });

  enriched.sort((a, b) => a.distanceFromObserverKm! - b.distanceFromObserverKm!);
  return enriched.slice(0, maxResults);
}
