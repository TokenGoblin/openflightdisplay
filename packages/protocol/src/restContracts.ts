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
export const Core2StatusResponseSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  deviceId: z.string().min(1),
  firmwareVersion: z.string().min(1),
  wifiState: z.enum(["connected", "disconnected", "provisioning"]),
  gatewayConnectionState: z.enum(["connected", "connecting", "disconnected", "unconfigured"]),
  lastAircraftUpdateAgeSeconds: z.number().min(0).optional(),
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
