import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { FastifyInstance } from "fastify";
import { loadEnv } from "../src/config/env.js";
import { createLogger } from "../src/lib/logger.js";
import { DeviceStore } from "../src/lib/deviceStore.js";
import { buildApp } from "../src/server.js";
import type { Poller } from "../src/lib/poller.js";

let dir: string;
let app: FastifyInstance;
let poller: Poller;

beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ofd-gateway-routes-"));
  const env = loadEnv({ AVIATION_PROVIDER: "mock", DEVICE_STORE_PATH: join(dir, "devices.json"), LOG_LEVEL: "fatal" });
  const logger = createLogger(env);
  const deviceStore = new DeviceStore(env.DEVICE_STORE_PATH, logger);
  await deviceStore.load();
  ({ app, poller } = await buildApp(env, logger, deviceStore));
});

afterEach(async () => {
  poller.stop();
  await app.close();
  await rm(dir, { recursive: true, force: true });
});

describe("POST /api/v1/devices/:deviceId/claim", () => {
  it("claims a new device", async () => {
    const res = await app.inject({
      method: "POST",
      url: "/api/v1/devices/core2-abc123/claim",
      payload: { schemaVersion: 1, deviceId: "core2-abc123", deviceName: "Living Room", pairingToken: "tok-1" },
    });
    expect(res.statusCode).toBe(200);
  });

  it("rejects a mismatched deviceId between URL and body", async () => {
    const res = await app.inject({
      method: "POST",
      url: "/api/v1/devices/core2-abc123/claim",
      payload: { schemaVersion: 1, deviceId: "core2-different", deviceName: "X", pairingToken: "tok-1" },
    });
    expect(res.statusCode).toBe(400);
  });

  it("rejects re-claiming with a different token once already claimed", async () => {
    await app.inject({
      method: "POST",
      url: "/api/v1/devices/core2-abc123/claim",
      payload: { schemaVersion: 1, deviceId: "core2-abc123", deviceName: "Living Room", pairingToken: "tok-1" },
    });
    const res = await app.inject({
      method: "POST",
      url: "/api/v1/devices/core2-abc123/claim",
      payload: { schemaVersion: 1, deviceId: "core2-abc123", deviceName: "Living Room", pairingToken: "tok-2" },
    });
    expect(res.statusCode).toBe(409);
  });
});

describe("GET/PUT /api/v1/devices/:deviceId/config", () => {
  async function claim() {
    await app.inject({
      method: "POST",
      url: "/api/v1/devices/core2-abc123/claim",
      payload: { schemaVersion: 1, deviceId: "core2-abc123", deviceName: "Living Room", pairingToken: "tok-1" },
    });
  }

  it("rejects config reads/writes without a valid pairing token", async () => {
    await claim();
    const res = await app.inject({ method: "GET", url: "/api/v1/devices/core2-abc123/config" });
    expect(res.statusCode).toBe(401);
  });

  it("allows config read/write with a valid token, and rejects an invalid config body", async () => {
    await claim();
    const getRes = await app.inject({
      method: "GET",
      url: "/api/v1/devices/core2-abc123/config",
      headers: { authorization: "Bearer tok-1" },
    });
    expect(getRes.statusCode).toBe(200);

    const putRes = await app.inject({
      method: "PUT",
      url: "/api/v1/devices/core2-abc123/config",
      headers: { authorization: "Bearer tok-1" },
      payload: {
        schemaVersion: 1,
        config: {
          deviceId: "core2-abc123",
          deviceName: "Updated Name",
          monitoringArea: { kind: "circle", centerLat: 47.6, centerLon: -122.3, radiusKm: 15 },
          displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
        },
      },
    });
    expect(putRes.statusCode).toBe(200);

    const badPutRes = await app.inject({
      method: "PUT",
      url: "/api/v1/devices/core2-abc123/config",
      headers: { authorization: "Bearer tok-1" },
      payload: { schemaVersion: 1, config: { deviceId: "core2-abc123", monitoringArea: { kind: "circle" } } },
    });
    expect(badPutRes.statusCode).toBe(400);
  });
});

describe("GET /api/v1/status", () => {
  it("reports provider and connection status", async () => {
    const res = await app.inject({ method: "GET", url: "/api/v1/status" });
    expect(res.statusCode).toBe(200);
    const body = res.json();
    expect(body.provider.id).toBe("mock");
    expect(body.connectedDevices).toBe(0);
  });
});
