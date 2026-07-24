import { describe, expect, it } from "vitest";
import {
  PairClaimRequestSchema,
  Core2StatusResponseSchema,
  GatewayStatusResponseSchema,
  CURRENT_SCHEMA_VERSION,
} from "../src/index.js";

describe("PairClaimRequestSchema", () => {
  it("requires exactly 6 digits", () => {
    expect(
      PairClaimRequestSchema.parse({ schemaVersion: CURRENT_SCHEMA_VERSION, code: "482913" }).code,
    ).toBe("482913");
    expect(() =>
      PairClaimRequestSchema.parse({ schemaVersion: CURRENT_SCHEMA_VERSION, code: "abc" }),
    ).toThrow();
    expect(() =>
      PairClaimRequestSchema.parse({ schemaVersion: CURRENT_SCHEMA_VERSION, code: "12345" }),
    ).toThrow();
  });
});

describe("Core2StatusResponseSchema", () => {
  it("accepts a well-formed status payload", () => {
    const status = Core2StatusResponseSchema.parse({
      schemaVersion: CURRENT_SCHEMA_VERSION,
      deviceId: "core2-abc123",
      firmwareVersion: "0.1.0",
      wifiState: "connected",
      gatewayConnectionState: "connected",
      lastAircraftUpdateAgeSeconds: 4,
      freeHeapBytes: 123456,
    });
    expect(status.wifiState).toBe("connected");
  });

  it("rejects an invalid wifiState", () => {
    expect(() =>
      Core2StatusResponseSchema.parse({
        schemaVersion: CURRENT_SCHEMA_VERSION,
        deviceId: "core2-abc123",
        firmwareVersion: "0.1.0",
        wifiState: "flying",
        gatewayConnectionState: "connected",
        freeHeapBytes: 123456,
      }),
    ).toThrow();
  });
});

describe("GatewayStatusResponseSchema", () => {
  it("accepts a well-formed gateway status payload", () => {
    const status = GatewayStatusResponseSchema.parse({
      schemaVersion: CURRENT_SCHEMA_VERSION,
      provider: { id: "adsblol", status: "ok", lastSuccessfulPollAt: "2026-07-24T12:00:00.000Z" },
      connectedDevices: 2,
    });
    expect(status.connectedDevices).toBe(2);
  });
});
