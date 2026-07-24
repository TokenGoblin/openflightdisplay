import { z } from "zod";

/**
 * Rendering configuration for a Core2 (and, later, the tablet's Core2
 * preview). Phase 1 only implements "single-aircraft" mode and idle/status
 * screens; other modes are modeled for forward compatibility with Phase 2/3.
 */
export const Core2DisplayModeSchema = z.enum([
  "single-aircraft",
  "compact-list",
  "flight-board",
  "minimal",
  "tracked-flight",
]);
export type Core2DisplayMode = z.infer<typeof Core2DisplayModeSchema>;

export const DisplayProfileSchema = z.object({
  mode: Core2DisplayModeSchema.default("single-aircraft"),
  brightness: z.number().min(10).max(255).default(200),
  units: z.literal("metric").default("metric"),
  use24HourClock: z.boolean().default(true),
});
export type DisplayProfile = z.infer<typeof DisplayProfileSchema>;
