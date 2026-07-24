import { readFile } from "node:fs/promises";
import { z } from "zod";
import { AircraftStateSchema, type CircleMonitoringArea } from "@openflightdisplay/shared-models";
import { ProviderFetchError, type AviationDataProvider, type RawProviderAircraft } from "./provider.js";

const FixtureSchema = z.object({
  throwsError: z.boolean().optional(),
  frames: z.array(z.array(AircraftStateSchema)),
});

/**
 * Plays back a recorded (sanitized) fixture file on a loop. Used for
 * deterministic demos and tests that need a specific scenario -- e.g.
 * "provider outage" (throwsError: true) or "aircraft with stale position"
 * (a frame whose positionTimestamp is old). See docs/TEST_PLAN.md for the
 * fixture list and tests/fixtures/ for the files themselves.
 */
export class ReplayProvider implements AviationDataProvider {
  readonly id = "replay";
  readonly requiresApiKey = false;
  readonly pollIntervalMs = 5_000;

  #fixturePath: string;
  #frames: RawProviderAircraft[][] | null = null;
  #throwsError = false;
  #frameIndex = 0;

  constructor(fixturePath: string) {
    this.#fixturePath = fixturePath;
  }

  async #load(): Promise<void> {
    if (this.#frames) return;
    const raw = await readFile(this.#fixturePath, "utf-8");
    const parsed = FixtureSchema.parse(JSON.parse(raw));
    this.#frames = parsed.frames;
    this.#throwsError = parsed.throwsError ?? false;
  }

  async fetchAircraft(_area: CircleMonitoringArea): Promise<RawProviderAircraft[]> {
    await this.#load();
    if (this.#throwsError) {
      throw new ProviderFetchError(this.id, "simulated provider outage from replay fixture");
    }
    const frames = this.#frames!;
    if (frames.length === 0) return [];
    const frame = frames[this.#frameIndex % frames.length]!;
    this.#frameIndex += 1;
    return frame;
  }
}
