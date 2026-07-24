import { z } from "zod";
import { AircraftCategorySchema } from "./aircraft.js";

/**
 * Composable aircraft filters. Phase 1 only applies `maxDistanceKm`
 * (implicitly, via the monitoring area radius) — every other field is
 * modeled now (per docs/FEATURE_PARITY_MATRIX.md) but not yet enforced by
 * gateway ranking/filtering logic. A filter profile with unimplemented
 * fields set is accepted and stored, but those fields are no-ops until
 * Phase 2+ wires them up — this is documented, not a silent bug.
 */
export const FilterProfileSchema = z.object({
  id: z.string().min(1),
  name: z.string().min(1).max(64),
  airborneOnly: z.boolean().optional(),
  maxDistanceKm: z.number().min(0).optional(),
  minAltitudeFt: z.number().min(0).optional(),
  maxAltitudeFt: z.number().min(0).optional(),
  categories: z.array(AircraftCategorySchema).optional(),
  callsignIncludes: z.array(z.string()).optional(),
  airlineCodes: z.array(z.string()).optional(),
  originAirports: z.array(z.string()).optional(),
  destinationAirports: z.array(z.string()).optional(),
  aircraftTypeCodes: z.array(z.string()).optional(),
  registrations: z.array(z.string()).optional(),
  icaoHexes: z.array(z.string()).optional(),
  squawks: z.array(z.string()).optional(),
  minSpeedKt: z.number().min(0).optional(),
  maxSpeedKt: z.number().min(0).optional(),
  verticalTrend: z.enum(["climbing", "descending", "level"]).optional(),
  favoritesOnly: z.boolean().optional(),
  excludeIcaoHexes: z.array(z.string()).optional(),
  excludeIncompleteData: z.boolean().optional(),
});
export type FilterProfile = z.infer<typeof FilterProfileSchema>;
