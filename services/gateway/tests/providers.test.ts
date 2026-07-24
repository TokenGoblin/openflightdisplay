import { describe, expect, it } from "vitest";
import { AircraftStateSchema } from "@openflightdisplay/shared-models";
import { MockProvider } from "../src/providers/mock.js";
import { ReplayProvider } from "../src/providers/replay.js";
import { ProviderFetchError } from "../src/providers/provider.js";

const area = { kind: "circle" as const, centerLat: 47.6, centerLon: -122.3, radiusKm: 20 };

describe("MockProvider", () => {
  it("produces aircraft that validate against AircraftStateSchema", async () => {
    const provider = new MockProvider();
    const aircraft = await provider.fetchAircraft(area);
    expect(aircraft.length).toBeGreaterThan(0);
    for (const a of aircraft) {
      expect(() => AircraftStateSchema.parse(a)).not.toThrow();
    }
  });

  it("moves aircraft between successive polls (not a frozen snapshot)", async () => {
    const provider = new MockProvider();
    const first = await provider.fetchAircraft(area);
    const second = await provider.fetchAircraft(area);
    expect(first[0]!.latitude).not.toEqual(second[0]!.latitude);
  });
});

describe("ReplayProvider", () => {
  it("plays back the no-aircraft fixture as an empty array", async () => {
    const provider = new ReplayProvider("tests/fixtures/no-aircraft.json");
    const aircraft = await provider.fetchAircraft(area);
    expect(aircraft).toEqual([]);
  });

  it("plays back the one-commercial-aircraft fixture and validates it", async () => {
    const provider = new ReplayProvider("tests/fixtures/one-commercial-aircraft.json");
    const aircraft = await provider.fetchAircraft(area);
    expect(aircraft).toHaveLength(1);
    expect(aircraft[0]!.callsign).toBe("UAL456");
  });

  it("loops back to the first frame after the last", async () => {
    const provider = new ReplayProvider("tests/fixtures/one-commercial-aircraft.json");
    await provider.fetchAircraft(area);
    const second = await provider.fetchAircraft(area);
    expect(second[0]!.callsign).toBe("UAL456");
  });

  it("throws a ProviderFetchError for the provider-outage fixture", async () => {
    const provider = new ReplayProvider("tests/fixtures/provider-outage.json");
    await expect(provider.fetchAircraft(area)).rejects.toBeInstanceOf(ProviderFetchError);
  });
});
