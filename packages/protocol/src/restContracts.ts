import { z } from "zod";
import { DeviceConfigurationSchema } from "@openflightdisplay/shared-models";
import { CURRENT_SCHEMA_VERSION } from "./envelope.js";

/** POST /pair (served by the Core2 itself) */
export const PairClaimRequestSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  code: z.string().regex(/^\d{6}$/),
});
export type PairClaimRequest = z.infer<typeof PairClaimRequestSchema>;

export const PairClaimResponseSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  pairingToken: z.string().min(1),
  deviceId: z.string().min(1),
});
export type PairClaimResponse = z.infer<typeof PairClaimResponseSchema>;

export const PairClaimErrorSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  error: z.literal("invalid_or_expired_code"),
});
export type PairClaimError = z.infer<typeof PairClaimErrorSchema>;

/** GET /api/v1/status (served by the Core2 itself, no auth) */
/**
 * Live tracked-flight state, as reported by the device.
 *
 * Derived, never configured — the write side is `trackedFlight` on
 * `DeviceConfiguration`. Every numeric field is optional because each
 * one genuinely may not exist yet: a flight that hasn't switched its
 * transponder on has no distance, and one stopped at the gate has no
 * ETA. See firmware/display/include/domain/flight_tracking.h.
 */
export const TrackedFlightStatusSchema = z.object({
  flight: z.string().min(1),
  callsign: z.string().min(1),
  destinationIcao: z.string().min(3),
  phase: z.enum(["WAITING", "ENROUTE", "DESCENDING", "APPROACHING", "LANDED", "NO CONTACT"]),
  /** False when the destination ICAO didn't resolve — i.e. a typo, not a flight yet to depart. */
  destinationResolved: z.boolean(),
  minutesRemaining: z.number().min(0).optional(),
  distanceToDestinationNm: z.number().min(0).optional(),
  secondsSinceContact: z.number().min(0).optional(),
});
export type TrackedFlightStatus = z.infer<typeof TrackedFlightStatusSchema>;

export const Core2StatusResponseSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  deviceId: z.string().min(1),
  firmwareVersion: z.string().min(1),
  wifiState: z.enum(["connected", "disconnected", "provisioning"]),
  /**
   * Health of the device's own data source. The firmware polls adsb.lol
   * directly, so this — not gatewayConnectionState — is what it actually
   * reports.
   */
  providerState: z.enum(["ok", "degraded", "unavailable"]).optional(),
  /**
   * Optional, and unset by current firmware. It was required here while
   * the device was a WebSocket client of the gateway; once it became a
   * standalone poller the field stopped being sent, and this schema was
   * never updated — which made `getCore2Status()` throw a ZodError
   * against any real device. Kept (optional) rather than deleted so a
   * gateway-connected mode can repopulate it without a schema bump.
   */
  gatewayConnectionState: z.enum(["connected", "connecting", "disconnected", "unconfigured"]).optional(),
  lastAircraftUpdateAgeSeconds: z.number().min(0).optional(),
  trackedFlight: TrackedFlightStatusSchema.optional(),
  freeHeapBytes: z.number().min(0),
});
export type Core2StatusResponse = z.infer<typeof Core2StatusResponseSchema>;

/** GET/PUT /api/v1/config (served by the Core2 itself, requires pairing token) */
export const Core2ConfigRequestSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  config: DeviceConfigurationSchema,
});
export type Core2ConfigRequest = z.infer<typeof Core2ConfigRequestSchema>;

/** POST /api/v1/devices/:deviceId/claim (served by the gateway) */
export const GatewayDeviceClaimRequestSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  deviceId: z.string().min(1),
  deviceName: z.string().min(1).max(64),
  pairingToken: z.string().min(1),
});
export type GatewayDeviceClaimRequest = z.infer<typeof GatewayDeviceClaimRequestSchema>;

/** GET /api/v1/status (served by the gateway) */
export const GatewayStatusResponseSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  provider: z.object({
    id: z.string().min(1),
    status: z.enum(["ok", "degraded", "unavailable"]),
    lastSuccessfulPollAt: z.string().datetime().optional(),
  }),
  connectedDevices: z.number().min(0),
});
export type GatewayStatusResponse = z.infer<typeof GatewayStatusResponseSchema>;
