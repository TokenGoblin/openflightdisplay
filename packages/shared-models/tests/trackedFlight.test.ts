import { describe, expect, it } from "vitest";
import { TrackedFlightSchema } from "../src/trackedFlight.js";
import { DeviceConfigurationSchema } from "../src/deviceConfiguration.js";

describe("TrackedFlightSchema", () => {
  it("accepts a flight number with an ICAO destination", () => {
    const tracked = TrackedFlightSchema.parse({
      flight: "UA1234",
      destinationIcao: "KSEA",
    });
    expect(tracked.flight).toBe("UA1234");
    expect(tracked.destinationIcao).toBe("KSEA");
  });

  it("accepts a raw ADS-B callsign as the flight", () => {
    expect(() => TrackedFlightSchema.parse({ flight: "UAL1234", destinationIcao: "KSEA" })).not.toThrow();
  });

  // The airport lookup that resolves this to coordinates answers null for
  // IATA, and "SEA" -> "KSEA" is a North America-only assumption, so the
  // code is rejected rather than guessed at.
  it("rejects an IATA destination code", () => {
    expect(() => TrackedFlightSchema.parse({ flight: "UA1234", destinationIcao: "SEA" })).toThrow();
  });

  it("rejects a destination containing digits", () => {
    expect(() => TrackedFlightSchema.parse({ flight: "UA1234", destinationIcao: "K5EA" })).toThrow();
  });

  it("requires a destination", () => {
    expect(() => TrackedFlightSchema.parse({ flight: "UA1234" })).toThrow();
  });

  // The device derives this; it is returned on read and must not be
  // required on write.
  it("treats the normalized callsign as optional", () => {
    const tracked = TrackedFlightSchema.parse({
      flight: "UA1234",
      callsign: "UAL1234",
      destinationIcao: "KSEA",
    });
    expect(tracked.callsign).toBe("UAL1234");
  });
});

describe("DeviceConfiguration tracked flight", () => {
  it("accepts a configuration with no tracked flight", () => {
    const config = DeviceConfigurationSchema.parse({ deviceId: "core2-abc123" });
    expect(config.trackedFlight).toBeUndefined();
  });

  // Absent and null mean different things on the wire: absent leaves
  // existing tracking alone, null stops it. Both must parse.
  it("accepts an explicit null to stop tracking", () => {
    const config = DeviceConfigurationSchema.parse({
      deviceId: "core2-abc123",
      trackedFlight: null,
    });
    expect(config.trackedFlight).toBeNull();
  });

  it("accepts a tracked flight and rejects a malformed one", () => {
    const config = DeviceConfigurationSchema.parse({
      deviceId: "tab5-1c40e2",
      trackedFlight: { flight: "BA249", destinationIcao: "EGLL" },
    });
    expect(config.trackedFlight?.destinationIcao).toBe("EGLL");

    expect(() =>
      DeviceConfigurationSchema.parse({
        deviceId: "tab5-1c40e2",
        trackedFlight: { flight: "BA249", destinationIcao: "LHR" },
      }),
    ).toThrow();
  });
});
