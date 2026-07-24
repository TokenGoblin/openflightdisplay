import Fastify, { type FastifyInstance } from "fastify";
import fastifyWebsocket from "@fastify/websocket";
import fastifyRateLimit from "@fastify/rate-limit";
import type { GatewayEnv } from "./config/env.js";
import type { Logger } from "./lib/logger.js";
import type { DeviceStore } from "./lib/deviceStore.js";
import { Poller } from "./lib/poller.js";
import { createProvider } from "./providers/index.js";
import { registerAircraftWebsocket } from "./ws/aircraftSocket.js";
import { registerDeviceRoutes } from "./routes/devices.js";
import { registerStatusRoute } from "./routes/status.js";

export interface BuildAppResult {
  app: FastifyInstance;
  poller: Poller;
}

export async function buildApp(env: GatewayEnv, logger: Logger, deviceStore: DeviceStore): Promise<BuildAppResult> {
  const app = Fastify({ loggerInstance: logger });

  await app.register(fastifyRateLimit, { global: true, max: 100, timeWindow: "1 minute" });
  await app.register(fastifyWebsocket);

  const provider = createProvider(env);
  const poller = new Poller(provider, deviceStore, logger);

  // Tighter rate limit specifically on the pairing/claim endpoint since a
  // brute-force attempt against it is the highest-value target on the LAN
  // (docs/SECURITY_AND_PRIVACY.md). This MUST be registered before the
  // routes it targets -- Fastify's onRoute hook only fires for routes
  // registered after the hook itself, not retroactively.
  app.addHook("onRoute", (routeOptions) => {
    if (routeOptions.url === "/api/v1/devices/:deviceId/claim" && routeOptions.method === "POST") {
      routeOptions.config = {
        ...routeOptions.config,
        rateLimit: { max: 5, timeWindow: "1 minute" },
      };
    }
  });

  const websocket = registerAircraftWebsocket(app, { deviceStore, poller, logger });
  registerDeviceRoutes(app, { deviceStore, logger });
  registerStatusRoute(app, { poller, websocket });

  return { app, poller };
}
