import Fastify, { type FastifyInstance } from "fastify";
import fastifyWebsocket from "@fastify/websocket";
import fastifyRateLimit from "@fastify/rate-limit";
import fastifyCors from "@fastify/cors";
import type { GatewayEnv } from "./config/env.js";
import type { Logger } from "./lib/logger.js";
import type { DeviceStore } from "./lib/deviceStore.js";
import { Poller } from "./lib/poller.js";
import { createProvider } from "./providers/index.js";
import { registerAircraftWebsocket, type AircraftWebsocketHandle } from "./ws/aircraftSocket.js";
import { registerDeviceRoutes } from "./routes/devices.js";
import { registerStatusRoute } from "./routes/status.js";

export interface BuildAppResult {
  app: FastifyInstance;
  poller: Poller;
  websocket: AircraftWebsocketHandle;
}

export async function buildApp(env: GatewayEnv, logger: Logger, deviceStore: DeviceStore): Promise<BuildAppResult> {
  // Fastify's own request logger is left at its default (off) -- every
  // route/module in this app takes the `logger` (our pino instance, with
  // secret redaction configured) as an explicit dependency instead, so
  // there's no need to wire a custom instance into Fastify itself here.
  const app = Fastify();

  // CORS: verified needed on real hardware -- the PWA (its own dev
  // server or static host origin) and the gateway are always different
  // origins from the browser's perspective (different port at minimum),
  // so every cross-origin fetch() from the PWA was silently blocked
  // before this, surfacing only as a generic "network error" with no
  // indication it was a CORS problem. `origin: true` reflects whatever
  // origin made the request -- appropriate for a LAN-only tool with no
  // sensitive cross-site exposure (see docs/SECURITY_AND_PRIVACY.md).
  await app.register(fastifyCors, {
    origin: true,
    methods: ["GET", "POST", "PUT", "OPTIONS"],
    allowedHeaders: ["Content-Type", "Authorization"],
  });
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

  return { app, poller, websocket };
}
