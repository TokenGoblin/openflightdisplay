import { z } from "zod";

export const ProviderHealthSchema = z.enum(["ok", "degraded", "unavailable"]);
export type ProviderHealth = z.infer<typeof ProviderHealthSchema>;

export const ProviderStatusSchema = z.object({
  providerId: z.string().min(1),
  status: ProviderHealthSchema,
  message: z.string().max(256).optional(),
  lastSuccessfulPollAt: z.string().datetime().optional(),
  consecutiveFailures: z.number().min(0).default(0),
});
export type ProviderStatus = z.infer<typeof ProviderStatusSchema>;
