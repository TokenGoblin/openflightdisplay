import { z } from "zod";

/**
 * Phase 3 feature (alerts). Modeled now, not implemented or read by any
 * Phase 1 code path.
 */
export const AlertTriggerSchema = z.enum([
  "area-entry",
  "aircraft-type",
  "favorite",
  "tracked-flight-detected",
  "emergency-squawk",
  "low-altitude",
  "helicopter-nearby",
  "military",
  "airline",
  "registration",
  "closest-approach",
]);
export type AlertTrigger = z.infer<typeof AlertTriggerSchema>;

export const AlertRuleSchema = z.object({
  id: z.string().min(1),
  trigger: AlertTriggerSchema,
  matchValue: z.string().optional(),
  channels: z
    .array(z.enum(["core2-screen", "core2-speaker", "core2-vibration", "browser-notification", "in-app"]))
    .min(1),
  cooldownSeconds: z.number().min(0).default(300),
  enabled: z.boolean().default(true),
});
export type AlertRule = z.infer<typeof AlertRuleSchema>;
