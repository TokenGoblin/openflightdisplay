import { z } from "zod";

/**
 * Phase 4 feature (opt-in local history). Modeled now, not implemented or
 * written by any Phase 1 code path.
 */
export const AircraftHistoryRecordSchema = z.object({
  icaoHex: z.string().regex(/^[0-9a-f]{6}$/i),
  firstSeen: z.string().datetime(),
  lastSeen: z.string().datetime(),
  closestDistanceKm: z.number().min(0).optional(),
  minAltitudeFt: z.number().optional(),
  maxSpeedKt: z.number().min(0).optional(),
  sightingCount: z.number().min(1).default(1),
});
export type AircraftHistoryRecord = z.infer<typeof AircraftHistoryRecordSchema>;
