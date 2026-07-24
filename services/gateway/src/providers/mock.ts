import type { CircleMonitoringArea } from "@openflightdisplay/shared-models";
import type { AviationDataProvider, RawProviderAircraft } from "./provider.js";

const KM_PER_DEGREE_LAT = 111.32;

/**
 * Synthetic aircraft generator with no network dependency. Produces a
 * small, deterministic set of plausible aircraft orbiting the requested
 * area's center, moving each poll -- used as the local-development default
 * and in every test that doesn't specifically target a real adapter.
 */
export class MockProvider implements AviationDataProvider {
  readonly id = "mock";
  readonly requiresApiKey = false;
  readonly pollIntervalMs = 5_000;

  #tick = 0;

  async fetchAircraft(area: CircleMonitoringArea): Promise<RawProviderAircraft[]> {
    this.#tick += 1;
    const now = new Date().toISOString();
    const kmPerDegreeLon = KM_PER_DEGREE_LAT * Math.cos((area.centerLat * Math.PI) / 180);

    const aircraft: RawProviderAircraft[] = [
      {
        provider: this.id,
        icaoHex: "a1b2c3",
        callsign: "MOCK123",
        aircraftTypeCode: "B738",
        aircraftCategory: "fixed-wing",
        latitude: area.centerLat + (2 / KM_PER_DEGREE_LAT) * Math.sin(this.#tick / 10),
        longitude: area.centerLon + (5 / kmPerDegreeLon) * Math.cos(this.#tick / 10),
        geometricAltitudeFt: 8500,
        groundSpeedKt: 240,
        trackHeadingDeg: (this.#tick * 4) % 360,
        verticalRateFtPerMin: -300,
        emergencyState: "none",
        onGround: false,
        firstSeen: now,
        lastSeen: now,
        positionTimestamp: now,
        dataQualityFlags: [],
      },
      {
        provider: this.id,
        icaoHex: "d4e5f6",
        callsign: "MOCK456",
        aircraftTypeCode: "C172",
        aircraftCategory: "fixed-wing",
        latitude: area.centerLat + (8 / KM_PER_DEGREE_LAT) * Math.cos(this.#tick / 20),
        longitude: area.centerLon + (8 / kmPerDegreeLon) * Math.sin(this.#tick / 20),
        geometricAltitudeFt: 2500,
        groundSpeedKt: 110,
        trackHeadingDeg: (this.#tick * 2 + 90) % 360,
        emergencyState: "none",
        onGround: false,
        firstSeen: now,
        lastSeen: now,
        positionTimestamp: now,
        dataQualityFlags: [],
      },
    ];

    return aircraft;
  }
}
