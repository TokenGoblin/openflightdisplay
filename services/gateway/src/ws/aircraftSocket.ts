import type { FastifyInstance } from "fastify";
import type { WebSocket } from "ws";
import {
  WS_HEARTBEAT_INTERVAL_MS,
  type AircraftUpdateMessage,
  type HeartbeatMessage,
  type ProviderStatusMessage,
} from "@openflightdisplay/protocol";
import type { DeviceStore } from "../lib/deviceStore.js";
import type { Poller, ProviderStatusEvent } from "../lib/poller.js";
import type { Logger } from "../lib/logger.js";
import type { AircraftState } from "@openflightdisplay/shared-models";

interface Deps {
  deviceStore: DeviceStore;
  poller: Poller;
  logger: Logger;
}

/**
 * WS /ws/v1/aircraft?deviceId=<id>&token=<pairingToken>
 * Registers both the Core2's and the PWA's connection to the same
 * per-device feed, guaranteeing they see the same aircraft (see
 * docs/ARCHITECTURE.md's "steady state data flow").
 */
export interface AircraftWebsocketHandle {
  getConnectedDeviceCount(): number;
}

export function registerAircraftWebsocket(app: FastifyInstance, deps: Deps): AircraftWebsocketHandle {
  const { deviceStore, poller, logger } = deps;
  const socketsByDevice = new Map<string, Set<WebSocket>>();

  function send(socket: WebSocket, message: AircraftUpdateMessage | HeartbeatMessage | ProviderStatusMessage): void {
    if (socket.readyState === socket.OPEN) {
      socket.send(JSON.stringify(message));
    }
  }

  poller.on("aircraft-update", (deviceId: string, aircraft: AircraftState[]) => {
    const sockets = socketsByDevice.get(deviceId);
    if (!sockets || sockets.size === 0) return;
    const message: AircraftUpdateMessage = {
      schemaVersion: 1,
      type: "aircraft-update",
      aircraft,
      generatedAt: new Date().toISOString(),
    };
    for (const socket of sockets) send(socket, message);
  });

  poller.on("provider-status", (event: ProviderStatusEvent) => {
    const message: ProviderStatusMessage = {
      schemaVersion: 1,
      type: "provider-status",
      provider: event.providerId,
      status: event.status,
      message: event.message,
    };
    for (const sockets of socketsByDevice.values()) {
      for (const socket of sockets) send(socket, message);
    }
  });

  setInterval(() => {
    const message: HeartbeatMessage = {
      schemaVersion: 1,
      type: "heartbeat",
      serverTime: new Date().toISOString(),
    };
    for (const sockets of socketsByDevice.values()) {
      for (const socket of sockets) send(socket, message);
    }
  }, WS_HEARTBEAT_INTERVAL_MS);

  app.get("/ws/v1/aircraft", { websocket: true }, (socket, req) => {
    const query = req.query as { deviceId?: string; token?: string };
    const { deviceId, token } = query;

    if (!deviceId || !token || !deviceStore.isValidToken(deviceId, token)) {
      logger.warn({ deviceId }, "rejected WS connection: invalid or missing pairing token");
      socket.close(4001, "invalid or missing pairing token");
      return;
    }

    let sockets = socketsByDevice.get(deviceId);
    if (!sockets) {
      sockets = new Set();
      socketsByDevice.set(deviceId, sockets);
    }
    sockets.add(socket);
    // Verified needed on real hardware: this is fire-and-forget (a
    // best-effort timestamp, not worth blocking the connection on), but
    // an unhandled rejection here previously crashed the entire gateway
    // process outright (Node terminates on unhandled rejections by
    // default) when persist() failed during a burst of rapid reconnects.
    // The race itself is now fixed in DeviceStore, but this is worth
    // keeping regardless -- "last seen" failing to save should never be
    // able to take down the whole process.
    deviceStore.touchLastSeen(deviceId, new Date()).catch((err) => {
      logger.warn({ err, deviceId }, "failed to update device last-seen timestamp");
    });
    logger.info({ deviceId }, "WS client connected");

    socket.on("close", () => {
      sockets?.delete(socket);
      logger.info({ deviceId }, "WS client disconnected");
    });

    socket.on("error", (err) => {
      logger.warn({ err, deviceId }, "WS socket error");
    });
  });

  return {
    getConnectedDeviceCount(): number {
      let count = 0;
      for (const sockets of socketsByDevice.values()) {
        if (sockets.size > 0) count += 1;
      }
      return count;
    },
  };
}
