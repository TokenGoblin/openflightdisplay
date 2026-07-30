import { z } from "zod";
import { AircraftStateListSchema } from "@openflightdisplay/shared-models";
import { CURRENT_SCHEMA_VERSION } from "./envelope.js";

/** Server -> client: bounded, ranked/filtered aircraft for this device. */
export const AircraftUpdateMessageSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  type: z.literal("aircraft-update"),
  aircraft: AircraftStateListSchema,
  generatedAt: z.string().datetime(),
});
export type AircraftUpdateMessage = z.infer<typeof AircraftUpdateMessageSchema>;

/** Server -> client: liveness. Sent every 15s (docs/PROTOCOL.md). */
export const HeartbeatMessageSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  type: z.literal("heartbeat"),
  serverTime: z.string().datetime(),
});
export type HeartbeatMessage = z.infer<typeof HeartbeatMessageSchema>;

/** Server -> client: explicit provider health, never a silent failure. */
export const ProviderStatusMessageSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  type: z.literal("provider-status"),
  provider: z.string().min(1),
  status: z.enum(["ok", "degraded", "unavailable"]),
  message: z.string().max(256).optional(),
});
export type ProviderStatusMessage = z.infer<typeof ProviderStatusMessageSchema>;

export const ServerToClientMessageSchema = z.discriminatedUnion("type", [
  AircraftUpdateMessageSchema,
  HeartbeatMessageSchema,
  ProviderStatusMessageSchema,
]);
export type ServerToClientMessage = z.infer<typeof ServerToClientMessageSchema>;

/** Client -> server: sent once immediately after connecting. */
export const HelloMessageSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
  type: z.literal("hello"),
  deviceId: z.string().min(1),
  /**
   * What kind of client this is. Firmware displays identify by board
   * ("core2", "tab5") rather than a single generic "display" value: the
   * gateway may eventually want to tailor payload size or update cadence
   * to the panel, and a Tab5 announcing itself as a Core2 would make that
   * impossible after the fact. Mirrors ofd::board::kDeviceIdPrefix in
   * firmware/display/include/board/board.h -- adding a board means adding
   * a value here.
   */
  role: z.enum(["core2", "tab5", "pwa"]),
});
export type HelloMessage = z.infer<typeof HelloMessageSchema>;

export const ClientToServerMessageSchema = z.discriminatedUnion("type", [HelloMessageSchema]);
export type ClientToServerMessage = z.infer<typeof ClientToServerMessageSchema>;

/** Reconnect policy constants both gateway clients (Core2, PWA) must honor. */
export const WS_HEARTBEAT_INTERVAL_MS = 15_000;
export const WS_DEAD_CONNECTION_TIMEOUT_MS = 45_000;
export const WS_RECONNECT_BASE_DELAY_MS = 1_000;
export const WS_RECONNECT_MAX_DELAY_MS = 30_000;
