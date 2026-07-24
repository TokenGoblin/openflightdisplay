import type { FastifyInstance } from "fastify";
import { z } from "zod";
import { DeviceConfigurationSchema } from "@openflightdisplay/shared-models";
import { GatewayDeviceClaimRequestSchema } from "@openflightdisplay/protocol";
import type { DeviceStore } from "../lib/deviceStore.js";
import { requireValidPairingToken } from "../lib/auth.js";
import type { Logger } from "../lib/logger.js";

interface Deps {
  deviceStore: DeviceStore;
  logger: Logger;
}

const ParamsSchema = z.object({ deviceId: z.string().min(1) });

/**
 * Device claim + config CRUD, mirroring the Core2's own local API (see
 * docs/PROTOCOL.md). The PWA writes the same configuration to both the
 * Core2 directly and here, so the gateway knows what to rank/filter for
 * that device's WebSocket stream.
 */
export function registerDeviceRoutes(app: FastifyInstance, deps: Deps): void {
  const { deviceStore, logger } = deps;

  app.post("/api/v1/devices/:deviceId/claim", async (req, reply) => {
    const params = ParamsSchema.safeParse(req.params);
    const body = GatewayDeviceClaimRequestSchema.safeParse(req.body);
    if (!params.success || !body.success) {
      return reply.code(400).send({ schemaVersion: 1, error: "invalid_request" });
    }
    const { deviceId } = params.data;
    if (deviceId !== body.data.deviceId) {
      return reply.code(400).send({ schemaVersion: 1, error: "device_id_mismatch" });
    }

    const existing = deviceStore.get(deviceId);
    if (existing && existing.pairingToken !== body.data.pairingToken) {
      logger.warn({ deviceId }, "rejected re-claim with a token that doesn't match the existing one");
      return reply.code(409).send({ schemaVersion: 1, error: "already_claimed_with_different_token" });
    }

    const config = existing?.config ?? {
      deviceId,
      deviceName: body.data.deviceName,
      displayProfile: { mode: "single-aircraft" as const, brightness: 200, units: "metric" as const, use24HourClock: true },
    };
    await deviceStore.claim(deviceId, body.data.pairingToken, config);
    logger.info({ deviceId }, "device claimed");
    return reply.code(200).send({ schemaVersion: 1, deviceId });
  });

  app.get("/api/v1/devices/:deviceId/config", async (req, reply) => {
    const params = ParamsSchema.safeParse(req.params);
    if (!params.success) return reply.code(400).send({ schemaVersion: 1, error: "invalid_request" });
    const { deviceId } = params.data;
    if (!requireValidPairingToken(req, reply, deviceStore, deviceId)) return;

    const record = deviceStore.get(deviceId);
    if (!record) return reply.code(404).send({ schemaVersion: 1, error: "device_not_found" });
    return reply.code(200).send({ schemaVersion: 1, config: record.config });
  });

  app.put("/api/v1/devices/:deviceId/config", async (req, reply) => {
    const params = ParamsSchema.safeParse(req.params);
    if (!params.success) return reply.code(400).send({ schemaVersion: 1, error: "invalid_request" });
    const { deviceId } = params.data;
    if (!requireValidPairingToken(req, reply, deviceStore, deviceId)) return;

    const bodySchema = z.object({ schemaVersion: z.literal(1), config: DeviceConfigurationSchema });
    const body = bodySchema.safeParse(req.body);
    if (!body.success) {
      return reply.code(400).send({ schemaVersion: 1, error: "invalid_config", details: body.error.message });
    }
    if (body.data.config.deviceId !== deviceId) {
      return reply.code(400).send({ schemaVersion: 1, error: "device_id_mismatch" });
    }

    await deviceStore.upsertConfig(deviceId, body.data.config);
    logger.info({ deviceId }, "device config updated");
    return reply.code(200).send({ schemaVersion: 1, config: body.data.config });
  });
}
