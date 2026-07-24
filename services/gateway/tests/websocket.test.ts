import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { FastifyInstance } from "fastify";
import WebSocket from "ws";
import { loadEnv } from "../src/config/env.js";
import { createLogger } from "../src/lib/logger.js";
import { DeviceStore } from "../src/lib/deviceStore.js";
import { buildApp } from "../src/server.js";
import type { Poller } from "../src/lib/poller.js";

let dir: string;
let app: FastifyInstance;
let poller: Poller;
let port: number;

beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ofd-gateway-ws-"));
  const env = loadEnv({
    AVIATION_PROVIDER: "mock",
    DEVICE_STORE_PATH: join(dir, "devices.json"),
    LOG_LEVEL: "fatal",
    PORT: "0",
  });
  const logger = createLogger(env);
  const deviceStore = new DeviceStore(env.DEVICE_STORE_PATH, logger);
  await deviceStore.load();
  await deviceStore.claim("core2-abc123", "tok-1", {
    deviceId: "core2-abc123",
    deviceName: "Living Room",
    monitoringArea: { kind: "circle", centerLat: 47.6, centerLon: -122.3, radiusKm: 20 },
    displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
  });
  ({ app, poller } = await buildApp(env, logger, deviceStore));
  await app.listen({ host: "127.0.0.1", port: 0 });
  const address = app.server.address();
  port = typeof address === "object" && address ? address.port : 0;
  poller.start();
});

afterEach(async () => {
  poller.stop();
  await app.close();
  await rm(dir, { recursive: true, force: true });
});

describe("WS /ws/v1/aircraft", () => {
  it("rejects a connection with an invalid pairing token", async () => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/ws/v1/aircraft?deviceId=core2-abc123&token=wrong`);
    const closeCode = await new Promise<number>((resolve) => {
      ws.on("close", (code) => resolve(code));
      ws.on("open", () => {
        /* should not fire */
      });
    });
    expect(closeCode).toBe(4001);
  });

  it("delivers a versioned aircraft-update message to a validly authenticated client", async () => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/ws/v1/aircraft?deviceId=core2-abc123&token=tok-1`);
    const message = await new Promise<Record<string, unknown>>((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error("timed out waiting for aircraft-update")), 5000);
      ws.on("message", (data) => {
        const parsed = JSON.parse(data.toString());
        if (parsed.type === "aircraft-update") {
          clearTimeout(timeout);
          resolve(parsed);
        }
      });
      ws.on("error", reject);
    });
    expect(message.schemaVersion).toBe(1);
    expect(Array.isArray(message.aircraft)).toBe(true);
    ws.close();
  });
});
