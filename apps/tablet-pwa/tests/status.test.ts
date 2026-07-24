import { describe, expect, it } from "vitest";
import { deriveStatus } from "../src/lib/status";
import type { AircraftFeedState } from "../src/hooks/useAircraftFeed";

const baseFeed: AircraftFeedState = {
  connectionState: "connected",
  aircraft: [],
  lastUpdatedAt: null,
  providerStatus: null,
};

describe("deriveStatus", () => {
  it("returns configuration-required when there is no config yet", () => {
    expect(deriveStatus(baseFeed, false)).toBe("configuration-required");
  });

  it("returns connecting when the WS isn't connected", () => {
    expect(deriveStatus({ ...baseFeed, connectionState: "connecting" }, true)).toBe("connecting");
    expect(deriveStatus({ ...baseFeed, connectionState: "disconnected" }, true)).toBe("connecting");
  });

  it("returns data-source-unavailable when the provider reports unavailable", () => {
    const feed: AircraftFeedState = {
      ...baseFeed,
      providerStatus: { provider: "adsblol", status: "unavailable" },
    };
    expect(deriveStatus(feed, true)).toBe("data-source-unavailable");
  });

  it("returns waiting-for-first-data before any update has arrived", () => {
    expect(deriveStatus(baseFeed, true)).toBe("waiting-for-first-data");
  });

  it("returns no-matching-aircraft when an update arrived with zero aircraft", () => {
    const feed: AircraftFeedState = { ...baseFeed, lastUpdatedAt: new Date() };
    expect(deriveStatus(feed, true)).toBe("no-matching-aircraft");
  });

  it("returns stale when the last update is older than the threshold", () => {
    const oldUpdate = new Date(Date.now() - 120_000);
    const feed: AircraftFeedState = {
      ...baseFeed,
      lastUpdatedAt: oldUpdate,
      aircraft: [{ provider: "mock", icaoHex: "abc123" } as never],
    };
    expect(deriveStatus(feed, true)).toBe("stale");
  });

  it("returns showing-aircraft when everything is healthy", () => {
    const feed: AircraftFeedState = {
      ...baseFeed,
      lastUpdatedAt: new Date(),
      aircraft: [{ provider: "mock", icaoHex: "abc123" } as never],
    };
    expect(deriveStatus(feed, true)).toBe("showing-aircraft");
  });
});
