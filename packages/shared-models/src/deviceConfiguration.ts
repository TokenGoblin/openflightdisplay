import { z } from "zod";
import { MonitoringAreaSchema } from "./monitoringArea.js";
import { DisplayProfileSchema } from "./displayProfile.js";

/**
 * A single Core2's configuration, as stored on the device (LittleFS) and
 * mirrored on the gateway (so the gateway knows what to rank/filter for
 * that device's WebSocket stream). See docs/PROTOCOL.md for the wire
 * shape this maps to.
 */
export const DeviceConfigurationSchema = z.object({
  deviceId: z.string().min(1),
  deviceName: z.string().min(1).max(64).default("OpenFlightDisplay"),
  gatewayUrl: z.string().url().optional(),
  monitoringArea: MonitoringAreaSchema.optional(),
  filterProfileId: z.string().optional(),
  displayProfile: DisplayProfileSchema.default({
    mode: "single-aircraft",
    brightness: 200,
    units: "metric",
    use24HourClock: true,
  }),
});
export type DeviceConfiguration = z.infer<typeof DeviceConfigurationSchema>;
