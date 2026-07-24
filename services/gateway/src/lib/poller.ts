import { EventEmitter } from "node:events";
import { AircraftStateSchema, type AircraftState, type ProviderHealth } from "@openflightdisplay/shared-models";
import type { AviationDataProvider } from "../providers/provider.js";
import type { DeviceStore } from "./deviceStore.js";
import type { Logger } from "./logger.js";
import { rankNearest } from "./ranking.js";

const DEGRADED_AFTER_FAILURES = 1;
const UNAVAILABLE_AFTER_FAILURES = 3;

export interface ProviderStatusEvent {
  providerId: string;
  status: ProviderHealth;
  message?: string;
}

/**
 * Polls the active provider on its own interval, once per claimed device
 * that has a (Phase-1-supported) circular monitoring area, validates and
 * ranks the result, and emits events the WebSocket layer broadcasts.
 * Never clears a device's last-known aircraft on a failed poll -- it
 * emits a provider-status event instead, so receivers can show
 * "data source unavailable, showing data from Xs ago" rather than
 * silently blanking (see docs/PROTOCOL.md).
 */
export class Poller extends EventEmitter {
  #provider: AviationDataProvider;
  #deviceStore: DeviceStore;
  #logger: Logger;
  #timer: NodeJS.Timeout | undefined;
  #consecutiveFailures = 0;
  #lastStatus: ProviderHealth = "ok";
  #lastSuccessfulPollAt: string | undefined;

  constructor(provider: AviationDataProvider, deviceStore: DeviceStore, logger: Logger) {
    super();
    this.#provider = provider;
    this.#deviceStore = deviceStore;
    this.#logger = logger;
  }

  start(): void {
    this.#timer = setInterval(() => {
      void this.pollOnce();
    }, this.#provider.pollIntervalMs);
    void this.pollOnce();
  }

  stop(): void {
    if (this.#timer) clearInterval(this.#timer);
  }

  async pollOnce(): Promise<void> {
    for (const record of this.#deviceStore.getAll()) {
      const area = record.config.monitoringArea;
      if (!area) continue;
      if (area.kind !== "circle") {
        this.#logger.warn(
          { deviceId: record.config.deviceId, kind: area.kind },
          "monitoring area kind not yet supported for polling; skipping (Phase 1 only implements circle)",
        );
        continue;
      }

      try {
        const raw = await this.#provider.fetchAircraft(area);
        const validated: AircraftState[] = raw.map((a) => AircraftStateSchema.parse(a));
        const ranked = rankNearest(validated, area);
        this.#lastSuccessfulPollAt = new Date().toISOString();
        this.#reportStatus("ok");
        this.emit("aircraft-update", record.config.deviceId, ranked);
      } catch (err) {
        this.#consecutiveFailures += 1;
        const status: ProviderHealth =
          this.#consecutiveFailures >= UNAVAILABLE_AFTER_FAILURES
            ? "unavailable"
            : this.#consecutiveFailures >= DEGRADED_AFTER_FAILURES
              ? "degraded"
              : "ok";
        this.#logger.error({ err, deviceId: record.config.deviceId }, "provider fetch failed");
        this.#reportStatus(status, err instanceof Error ? err.message : String(err));
      }
    }
  }

  #reportStatus(status: ProviderHealth, message?: string): void {
    if (status === "ok") this.#consecutiveFailures = 0;
    if (status === this.#lastStatus) return;
    this.#lastStatus = status;
    const event: ProviderStatusEvent = { providerId: this.#provider.id, status, message };
    this.emit("provider-status", event);
  }

  getStatus(): { id: string; status: ProviderHealth; lastSuccessfulPollAt?: string } {
    return {
      id: this.#provider.id,
      status: this.#lastStatus,
      lastSuccessfulPollAt: this.#lastSuccessfulPollAt,
    };
  }
}
