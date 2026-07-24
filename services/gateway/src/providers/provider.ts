import type { AircraftState, CircleMonitoringArea } from "@openflightdisplay/shared-models";

/**
 * Every provider adapter returns already-normalized AircraftState records
 * (see docs/PROVIDER_ADAPTERS.md). Central validation against
 * AircraftStateSchema happens once, in the poller (src/lib/poller.ts),
 * not duplicated in each adapter -- that's the "one shared
 * normalization/validation point" referenced in the docs.
 */
export type RawProviderAircraft = AircraftState;

export interface AviationDataProvider {
  readonly id: string;
  readonly requiresApiKey: boolean;
  readonly pollIntervalMs: number;
  fetchAircraft(area: CircleMonitoringArea): Promise<RawProviderAircraft[]>;
}

export class ProviderFetchError extends Error {
  constructor(
    public readonly providerId: string,
    message: string,
    cause?: unknown,
  ) {
    // `cause` is a real Error.cause (ES2022), passed via the standard
    // options bag -- redeclaring it as a parameter property here instead
    // would conflict with the base class's own `cause` and require an
    // `override` modifier for no benefit.
    super(`[${providerId}] ${message}`, cause !== undefined ? { cause } : undefined);
    this.name = "ProviderFetchError";
  }
}
