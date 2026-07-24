import { z } from "zod";

/**
 * Phase 3 feature (individual flight tracking). Modeled now so the
 * DeviceConfiguration shape doesn't need a breaking migration later;
 * not read or written by any Phase 1 code path.
 */
export const TrackedFlightSchema = z.object({
  id: z.string().min(1),
  matchBy: z.enum(["flightNumber", "callsign", "registration", "icaoHex"]),
  matchValue: z.string().min(1),
  createdAt: z.string().datetime(),
  expiresAt: z.string().datetime().optional(),
});
export type TrackedFlight = z.infer<typeof TrackedFlightSchema>;
