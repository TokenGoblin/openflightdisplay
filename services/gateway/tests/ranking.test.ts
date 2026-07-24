import { describe, expect, it } from "vitest";
import type { AircraftState } from "@openflightdisplay/shared-models";
import { rankNearest, isStale, STALE_POSITION_THRESHOLD_MS } from "../src/lib/ranking.js";

const area = { kind: "circle" as const, centerLat: 47.6, centerLon: -122.3, radiusKm: 10 };

function makeAircraft(icaoHex: string, latitude: number, longitude: number): AircraftState {
  const now = "2026-07-24T12:00:00.000Z";
  return {
    provider: "test",
    icaoHex,
    latitude,
    longitude,
    emergencyState: "none",
    onGround: false,
    firstSeen: now,
    lastSeen: now,
    positionTimestamp: now,
    dataQualityFlags: [],
  };
}

describe("rankNearest", () => {
  it("orders aircraft by distance from the area center, nearest first", () => {
    const far = makeAircraft("aaaaaa", 47.65, -122.35);
    const near = makeAircraft("bbbbbb", 47.601, -122.301);
    const ranked = rankNearest([far, near], area);
    expect(ranked[0]!.icaoHex).toBe("bbbbbb");
    expect(ranked.map((a) => a.icaoHex)).toEqual(["bbbbbb", "aaaaaa"]);
  });

  it("excludes aircraft outside the monitoring radius", () => {
    const outside = makeAircraft("cccccc", 48.5, -123.5);
    const ranked = rankNearest([outside], area);
    expect(ranked).toEqual([]);
  });

  it("fills in distanceFromObserverKm and bearingFromObserverDeg", () => {
    const near = makeAircraft("bbbbbb", 47.61, -122.3);
    const [result] = rankNearest([near], area);
    expect(result!.distanceFromObserverKm).toBeGreaterThan(0);
    expect(result!.bearingFromObserverDeg).toBeGreaterThanOrEqual(0);
    expect(result!.bearingFromObserverDeg).toBeLessThan(360);
  });

  it("caps results at maxResults", () => {
    const many = Array.from({ length: 5 }, (_, i) => makeAircraft(`${i}${i}${i}${i}${i}${i}`, 47.601, -122.301));
    const ranked = rankNearest(many, area, 2);
    expect(ranked).toHaveLength(2);
  });
});

describe("isStale", () => {
  it("flags a position older than the threshold", () => {
    const oldTimestamp = new Date(Date.now() - STALE_POSITION_THRESHOLD_MS - 1000).toISOString();
    expect(isStale({ positionTimestamp: oldTimestamp }, new Date())).toBe(true);
  });

  it("does not flag a fresh position", () => {
    expect(isStale({ positionTimestamp: new Date().toISOString() }, new Date())).toBe(false);
  });
});
