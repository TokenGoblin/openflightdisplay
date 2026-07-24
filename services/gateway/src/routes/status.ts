import type { FastifyInstance } from "fastify";
import type { GatewayStatusResponse } from "@openflightdisplay/protocol";
import type { Poller } from "../lib/poller.js";
import type { AircraftWebsocketHandle } from "../ws/aircraftSocket.js";

interface Deps {
  poller: Poller;
  websocket: AircraftWebsocketHandle;
}

export function registerStatusRoute(app: FastifyInstance, deps: Deps): void {
  app.get("/api/v1/status", async (_req, reply) => {
    const providerStatus = deps.poller.getStatus();
    const body: GatewayStatusResponse = {
      schemaVersion: 1,
      provider: providerStatus,
      connectedDevices: deps.websocket.getConnectedDeviceCount(),
    };
    return reply.code(200).send(body);
  });
}
