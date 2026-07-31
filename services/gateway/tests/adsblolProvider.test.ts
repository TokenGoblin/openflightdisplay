import { describe, expect, it, vi, afterEach } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { AircraftStateSchema } from "@openflightdisplay/shared-models";
import type { CircleMonitoringArea } from "@openflightdisplay/shared-models";
import { AdsbLolProvider } from "../src/providers/adsblol.js";
import { ProviderFetchError } from "../src/providers/provider.js";

/**
 * The adsb.lol adapter had no test at all until this file, despite being
 * the only provider that runs in production and the one doing by far the
 * most parsing. `docs/TEST_PLAN.md` claimed it was covered "from a
 * recorded sample response fixture"; no such fixture or test existed.
 *
 * The fixture is synthetic in its values and exact in its shape — every
 * structural quirk asserted below was observed in a real response
 * captured from api.adsb.lol. That matters more than usual here: the
 * firmware shipped with a callsign-padding bug that a
 * realistically-shaped fixture would have caught immediately.
 *
 * No network access: fetch is stubbed, so this runs in CI.
 */

const fixture = JSON.parse(
  readFileSync(fileURLToPath(new URL("./fixtures/adsblol-point-response.json", import.meta.url)), "utf8"),
);

const area: CircleMonitoringArea = {
  kind: "circle",
  centerLat: 51.47,
  centerLon: -0.45,
  radiusKm: 40,
};

function stubFetch(body: unknown, init: { ok?: boolean; status?: number; json?: () => unknown } = {}) {
  const spy = vi.fn(async () => ({
    ok: init.ok ?? true,
    status: init.status ?? 200,
    json: init.json ?? (async () => body),
  }));
  vi.stubGlobal("fetch", spy);
  return spy;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("AdsbLolProvider request", () => {
  it("queries the point endpoint with the radius converted to nautical miles", async () => {
    const spy = stubFetch(fixture);
    await new AdsbLolProvider().fetchAircraft(area);

    const url = String(spy.mock.calls[0]?.[0]);
    expect(url).toContain("/v2/point/51.47/-0.45/");
    // 40 km is ~21.6 NM — the API takes nautical miles, the config is metric.
    expect(url).toMatch(/\/21\.6$/);
  });

  // Guards a free, community-funded data source from a config that asks
  // for the whole continent.
  it("clamps an extreme radius", async () => {
    const spy = stubFetch(fixture);
    await new AdsbLolProvider().fetchAircraft({ ...area, radiusKm: 99_999 });
    expect(String(spy.mock.calls[0]?.[0])).toMatch(/\/250\.0$/);
  });
});

describe("AdsbLolProvider normalization", () => {
  it("produces records that validate against the shared AircraftState schema", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    expect(aircraft.length).toBeGreaterThan(0);
    for (const a of aircraft) {
      expect(() => AircraftStateSchema.parse(a)).not.toThrow();
    }
  });

  // The exact bug the firmware shipped with. ADS-B pads callsigns to
  // eight characters; it is invisible in a left-aligned label and fatal
  // to any comparison.
  it("strips the space padding ADS-B puts on callsigns", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    const callsigns = aircraft.map((a) => a.callsign).filter(Boolean);
    expect(callsigns).toContain("TST1234");
    for (const cs of callsigns) {
      expect(cs).toBe(cs!.trim());
    }
  });

  // alt_baro is the *string* "ground", not a number, for surface traffic.
  // Treating it as an altitude would report an aircraft at 0 ft rather
  // than on the ground.
  it("reads the string alt_baro 'ground' as on-ground with no altitude", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    const grounded = aircraft.find((a) => a.icaoHex === "d4e5f6");
    expect(grounded?.onGround).toBe(true);
    expect(grounded?.barometricAltitudeFt).toBeUndefined();
    expect(grounded?.dataQualityFlags).toContain("no-altitude");
  });

  it("flags a record with no callsign rather than inventing one", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    const anonymous = aircraft.find((a) => a.icaoHex === "0a0b0c");
    expect(anonymous?.callsign).toBeUndefined();
    expect(anonymous?.dataQualityFlags).toContain("no-callsign");
  });

  it("falls back to geometric vertical rate when the barometric one is absent", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    expect(aircraft.find((a) => a.icaoHex === "0a0b0c")?.verticalRateFtPerMin).toBe(320);
  });

  it("maps an emergency squawk to the shared emergency state", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    expect(aircraft.find((a) => a.icaoHex === "beef01")?.emergencyState).toBe("general");
  });

  // A record with no position is useless to every consumer, and emitting
  // it would put an aircraft at (0, 0) — in the Gulf of Guinea.
  it("drops records with no usable position", async () => {
    stubFetch(fixture);
    const aircraft = await new AdsbLolProvider().fetchAircraft(area);
    expect(aircraft.some((a) => a.callsign === "NOPOS")).toBe(false);
    expect(aircraft).toHaveLength(4);
  });
});

describe("AdsbLolProvider failure handling", () => {
  it("raises ProviderFetchError on a non-200", async () => {
    stubFetch(null, { ok: false, status: 503 });
    await expect(new AdsbLolProvider().fetchAircraft(area)).rejects.toBeInstanceOf(ProviderFetchError);
  });

  it("raises ProviderFetchError on a malformed body rather than throwing raw", async () => {
    stubFetch(null, {
      json: () => {
        throw new SyntaxError("Unexpected token");
      },
    });
    await expect(new AdsbLolProvider().fetchAircraft(area)).rejects.toBeInstanceOf(ProviderFetchError);
  });

  it("raises ProviderFetchError when the request itself fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("ECONNREFUSED"); }));
    await expect(new AdsbLolProvider().fetchAircraft(area)).rejects.toBeInstanceOf(ProviderFetchError);
  });

  // A 200 with an unexpected body shape yields nothing rather than
  // throwing — a provider that changes its envelope shouldn't crash the
  // poller, it should just report no aircraft.
  it("returns an empty list for a well-formed response with no 'ac' array", async () => {
    stubFetch({ msg: "No error", total: 0 });
    await expect(new AdsbLolProvider().fetchAircraft(area)).resolves.toEqual([]);
  });
});
