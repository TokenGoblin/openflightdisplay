import { describe, expect, it } from "vitest";
import {
  ServerToClientMessageSchema,
  ClientToServerMessageSchema,
  CURRENT_SCHEMA_VERSION,
} from "../src/index.js";

describe("ServerToClientMessageSchema", () => {
  it("accepts a well-formed aircraft-update message", () => {
    const msg = ServerToClientMessageSchema.parse({
      schemaVersion: CURRENT_SCHEMA_VERSION,
      type: "aircraft-update",
      aircraft: [],
      generatedAt: "2026-07-24T12:00:00.000Z",
    });
    expect(msg.type).toBe("aircraft-update");
  });

  it("accepts a heartbeat message", () => {
    const msg = ServerToClientMessageSchema.parse({
      schemaVersion: CURRENT_SCHEMA_VERSION,
      type: "heartbeat",
      serverTime: "2026-07-24T12:00:00.000Z",
    });
    expect(msg.type).toBe("heartbeat");
  });

  it("accepts a provider-status message and rejects an unknown status enum value", () => {
    const msg = ServerToClientMessageSchema.parse({
      schemaVersion: CURRENT_SCHEMA_VERSION,
      type: "provider-status",
      provider: "adsblol",
      status: "unavailable",
      message: "adsb.lol unreachable, retrying",
    });
    expect(msg.type).toBe("provider-status");

    expect(() =>
      ServerToClientMessageSchema.parse({
        schemaVersion: CURRENT_SCHEMA_VERSION,
        type: "provider-status",
        provider: "adsblol",
        status: "on-fire",
      }),
    ).toThrow();
  });

  it("rejects an unrecognized schemaVersion rather than best-effort parsing", () => {
    expect(() =>
      ServerToClientMessageSchema.parse({
        schemaVersion: 999,
        type: "heartbeat",
        serverTime: "2026-07-24T12:00:00.000Z",
      }),
    ).toThrow();
  });

  it("rejects an unknown message type", () => {
    expect(() =>
      ServerToClientMessageSchema.parse({
        schemaVersion: CURRENT_SCHEMA_VERSION,
        type: "something-else",
      }),
    ).toThrow();
  });
});

describe("ClientToServerMessageSchema", () => {
  it("accepts a well-formed hello message from either role", () => {
    for (const role of ["core2", "pwa"] as const) {
      const msg = ClientToServerMessageSchema.parse({
        schemaVersion: CURRENT_SCHEMA_VERSION,
        type: "hello",
        deviceId: "core2-abc123",
        role,
      });
      expect(msg.role).toBe(role);
    }
  });
});
