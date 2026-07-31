import { z } from "zod";
import { MonitoringAreaSchema } from "./monitoringArea.js";
import { DisplayProfileSchema } from "./displayProfile.js";
import { TrackedFlightSchema } from "./trackedFlight.js";

/**
 * A single Core2's configuration, as stored on the device (LittleFS) and
 * mirrored on the gateway (so the gateway knows what to rank/filter for
 * that device's WebSocket stream). See docs/PROTOCOL.md for the wire
 * shape this maps to.
 */
export const DeviceConfigurationSchema = z.object({
  deviceId: z.string().min(1),
  deviceName: z.string().min(1).max(64).default("OpenFlightDisplay"),
  /**
   * @deprecated Nothing reads this. It dates from the design where the
   * device was a WebSocket client of the gateway; the firmware has had
   * no corresponding config field since it became a standalone poller,
   * and the gateway never read it either. The setup wizard used to
   * compute and transmit it on every pairing for no effect.
   *
   * Kept optional rather than deleted so device configs persisted by
   * older builds still parse. Remove once no stored config can contain it.
   */
  gatewayUrl: z.string().url().optional(),
  monitoringArea: MonitoringAreaSchema.optional(),
  /**
   * A flight being followed to its destination, or null to stop.
   *
   * Three states, and the difference matters on the wire: the key being
   * *absent* leaves any existing tracking alone (so a PUT that only
   * changes brightness doesn't cancel somebody's airport run), an
   * explicit `null` stops tracking, and an object starts it. See
   * `docs/PROTOCOL.md`.
   */
  trackedFlight: TrackedFlightSchema.nullable().optional(),
  filterProfileId: z.string().optional(),
  displayProfile: DisplayProfileSchema.default({
    mode: "single-aircraft",
    brightness: 200,
    units: "metric",
    use24HourClock: true,
  }),
});
export type DeviceConfiguration = z.infer<typeof DeviceConfigurationSchema>;
