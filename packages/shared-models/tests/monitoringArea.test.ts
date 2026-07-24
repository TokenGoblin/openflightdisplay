import { describe, expect, it } from "vitest";
import { MonitoringAreaSchema } from "../src/monitoringArea.js";
import { DeviceConfigurationSchema } from "../src/deviceConfiguration.js";

describe("MonitoringAreaSchema", () => {
  it("accepts a valid circle", () => {
    const area = MonitoringAreaSchema.parse({
      kind: "circle",
      centerLat: 47.6,
      centerLon: -122.3,
      radiusKm: 15,
    });
    expect(area.kind).toBe("circle");
  });

  it("rejects a circle with an out-of-range radius", () => {
    expect(() =>
      MonitoringAreaSchema.parse({
        kind: "circle",
        centerLat: 47.6,
        centerLon: -122.3,
        radiusKm: 5000,
      }),
    ).toThrow();
  });

  it("accepts a polygon with at least 3 vertices and rejects fewer", () => {
    const triangle = {
      kind: "polygon" as const,
      vertices: [
        { lat: 47.6, lon: -122.3 },
        { lat: 47.7, lon: -122.3 },
        { lat: 47.65, lon: -122.2 },
      ],
    };
    expect(MonitoringAreaSchema.parse(triangle).kind).toBe("polygon");
    expect(() =>
      MonitoringAreaSchema.parse({ kind: "polygon", vertices: triangle.vertices.slice(0, 2) }),
    ).toThrow();
  });
});

describe("DeviceConfigurationSchema", () => {
  it("round-trips a full Phase 1 config", () => {
    const config = {
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      gatewayUrl: "ws://192.168.1.50:8787/ws/v1/aircraft",
      monitoringArea: { kind: "circle" as const, centerLat: 47.6, centerLon: -122.3, radiusKm: 15 },
    };
    const parsed = DeviceConfigurationSchema.parse(config);
    expect(parsed.displayProfile.mode).toBe("single-aircraft");
    expect(parsed.monitoringArea?.kind).toBe("circle");
    const reparsed = DeviceConfigurationSchema.parse(JSON.parse(JSON.stringify(parsed)));
    expect(reparsed).toEqual(parsed);
  });

  it("rejects an invalid gatewayUrl", () => {
    expect(() =>
      DeviceConfigurationSchema.parse({ deviceId: "x", gatewayUrl: "not-a-url" }),
    ).toThrow();
  });
});
