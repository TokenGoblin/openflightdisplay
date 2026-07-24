import { describe, expect, it } from "vitest";
import {
  AircraftStateSchema,
  AircraftStateListSchema,
  MAX_AIRCRAFT_PER_UPDATE,
} from "../src/aircraft.js";

const validAircraft = {
  provider: "mock",
  icaoHex: "a1b2c3",
  callsign: "UAL123",
  latitude: 47.6062,
  longitude: -122.3321,
  emergencyState: "none" as const,
  onGround: false,
  firstSeen: "2026-07-24T12:00:00.000Z",
  lastSeen: "2026-07-24T12:00:05.000Z",
  positionTimestamp: "2026-07-24T12:00:05.000Z",
  dataQualityFlags: [],
};

describe("AircraftStateSchema", () => {
  it("accepts a minimal valid record and fills in defaults", () => {
    const parsed = AircraftStateSchema.parse(validAircraft);
    expect(parsed.emergencyState).toBe("none");
    expect(parsed.onGround).toBe(false);
    expect(parsed.dataQualityFlags).toEqual([]);
  });

  it("rejects an invalid icaoHex", () => {
    expect(() =>
      AircraftStateSchema.parse({ ...validAircraft, icaoHex: "not-hex" }),
    ).toThrow();
  });

  it("rejects out-of-range latitude/longitude", () => {
    expect(() => AircraftStateSchema.parse({ ...validAircraft, latitude: 999 })).toThrow();
    expect(() => AircraftStateSchema.parse({ ...validAircraft, longitude: -999 })).toThrow();
  });

  it("rejects a squawk that isn't 4 octal digits", () => {
    expect(() =>
      AircraftStateSchema.parse({ ...validAircraft, squawk: "9999" }),
    ).toThrow();
    expect(AircraftStateSchema.parse({ ...validAircraft, squawk: "7700" }).squawk).toBe("7700");
  });

  it("allows optional fields to be omitted entirely", () => {
    const parsed = AircraftStateSchema.parse(validAircraft);
    expect(parsed.registration).toBeUndefined();
    expect(parsed.groundSpeedKt).toBeUndefined();
  });
});

describe("AircraftStateListSchema", () => {
  it("enforces the bounded max-length used for wire payloads", () => {
    const many = Array.from({ length: MAX_AIRCRAFT_PER_UPDATE + 1 }, (_, i) => ({
      ...validAircraft,
      icaoHex: i.toString(16).padStart(6, "0"),
    }));
    expect(() => AircraftStateListSchema.parse(many)).toThrow();

    const ok = many.slice(0, MAX_AIRCRAFT_PER_UPDATE);
    expect(AircraftStateListSchema.parse(ok)).toHaveLength(MAX_AIRCRAFT_PER_UPDATE);
  });
});
