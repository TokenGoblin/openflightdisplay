import { z } from "zod";

/**
 * Monitoring area shapes. Phase 1 only implements "circle" end-to-end;
 * "cone" and "polygon" are modeled now so Phase 3 doesn't need a breaking
 * protocol/schema migration, but are rejected by Phase 1 gateway/firmware
 * logic with a clear "not yet supported" error rather than silently
 * mis-handled.
 */
const CircleAreaSchema = z.object({
  kind: z.literal("circle"),
  centerLat: z.number().min(-90).max(90),
  centerLon: z.number().min(-180).max(180),
  radiusKm: z.number().min(0.5).max(500),
  minAltitudeFt: z.number().min(0).optional(),
  maxAltitudeFt: z.number().min(0).optional(),
});

const ConeAreaSchema = z.object({
  kind: z.literal("cone"),
  centerLat: z.number().min(-90).max(90),
  centerLon: z.number().min(-180).max(180),
  radiusKm: z.number().min(0.5).max(500),
  headingDeg: z.number().min(0).max(360),
  widthDeg: z.number().min(1).max(360),
  minAltitudeFt: z.number().min(0).optional(),
  maxAltitudeFt: z.number().min(0).optional(),
});

const PolygonAreaSchema = z.object({
  kind: z.literal("polygon"),
  vertices: z
    .array(z.object({ lat: z.number().min(-90).max(90), lon: z.number().min(-180).max(180) }))
    .min(3)
    .max(64),
  minAltitudeFt: z.number().min(0).optional(),
  maxAltitudeFt: z.number().min(0).optional(),
});

export const MonitoringAreaSchema = z.discriminatedUnion("kind", [
  CircleAreaSchema,
  ConeAreaSchema,
  PolygonAreaSchema,
]);
export type MonitoringArea = z.infer<typeof MonitoringAreaSchema>;
export type CircleMonitoringArea = z.infer<typeof CircleAreaSchema>;

export const NamedMonitoringAreaSchema = z.object({
  id: z.string().min(1),
  name: z.string().min(1).max(64),
  enabled: z.boolean().default(true),
  area: MonitoringAreaSchema,
});
export type NamedMonitoringArea = z.infer<typeof NamedMonitoringAreaSchema>;
