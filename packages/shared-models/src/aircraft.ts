import { z } from "zod";

/**
 * Normalized aircraft state, independent of any specific provider.
 * Field list matches docs/ARCHITECTURE.md's data model exactly.
 * Fields a provider didn't report are omitted (not sent as null) to keep
 * WebSocket payloads small for the Core2's bounded JSON parser.
 */
export const AircraftCategorySchema = z.enum([
  "fixed-wing",
  "rotorcraft",
  "glider",
  "balloon",
  "drone",
  "ground-vehicle",
  "unknown",
]);
export type AircraftCategory = z.infer<typeof AircraftCategorySchema>;

export const EmergencyStateSchema = z.enum([
  "none",
  "general",
  "medical",
  "minimum-fuel",
  "no-communications",
  "unlawful-interference",
  "downed",
]);
export type EmergencyState = z.infer<typeof EmergencyStateSchema>;

export const DataQualityFlagSchema = z.enum([
  "no-position",
  "no-callsign",
  "no-altitude",
  "stale-position",
  "estimated-position",
]);
export type DataQualityFlag = z.infer<typeof DataQualityFlagSchema>;

export const AircraftStateSchema = z.object({
  provider: z.string().min(1),
  icaoHex: z.string().regex(/^[0-9a-f]{6}$/i),

  callsign: z.string().max(16).optional(),
  registration: z.string().max(16).optional(),
  operator: z.string().max(64).optional(),
  airlineCode: z.string().max(8).optional(),
  flightNumber: z.string().max(16).optional(),

  aircraftTypeCode: z.string().max(8).optional(),
  aircraftDescription: z.string().max(64).optional(),
  aircraftCategory: AircraftCategorySchema.optional(),

  latitude: z.number().min(-90).max(90),
  longitude: z.number().min(-180).max(180),
  geometricAltitudeFt: z.number().optional(),
  barometricAltitudeFt: z.number().optional(),
  groundSpeedKt: z.number().min(0).optional(),
  trackHeadingDeg: z.number().min(0).max(360).optional(),
  verticalRateFtPerMin: z.number().optional(),
  squawk: z.string().regex(/^[0-7]{4}$/).optional(),
  emergencyState: EmergencyStateSchema.default("none"),
  onGround: z.boolean().default(false),

  originAirport: z.string().max(8).optional(),
  destinationAirport: z.string().max(8).optional(),

  distanceFromObserverKm: z.number().min(0).optional(),
  bearingFromObserverDeg: z.number().min(0).max(360).optional(),
  slantRangeKm: z.number().min(0).optional(),
  approaching: z.boolean().optional(),

  firstSeen: z.string().datetime(),
  lastSeen: z.string().datetime(),
  positionTimestamp: z.string().datetime(),
  enrichmentTimestamp: z.string().datetime().optional(),

  dataQualityFlags: z.array(DataQualityFlagSchema).default([]),
});

export type AircraftState = z.infer<typeof AircraftStateSchema>;

/** Bounded list bound sent over the wire (see docs/PROTOCOL.md). */
export const MAX_AIRCRAFT_PER_UPDATE = 10;

export const AircraftStateListSchema = z
  .array(AircraftStateSchema)
  .max(MAX_AIRCRAFT_PER_UPDATE);
