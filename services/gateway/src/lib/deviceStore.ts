import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import { z } from "zod";
import { DeviceConfigurationSchema, type DeviceConfiguration } from "@openflightdisplay/shared-models";
import type { Logger } from "./logger.js";

const DeviceRecordSchema = z.object({
  config: DeviceConfigurationSchema,
  pairingToken: z.string().min(1),
  lastSeenAt: z.string().datetime().optional(),
});
export type DeviceRecord = z.infer<typeof DeviceRecordSchema>;

const StoreFileSchema = z.record(z.string(), DeviceRecordSchema);
type StoreFile = z.infer<typeof StoreFileSchema>;

/**
 * Simple JSON-file-backed device config store for Phase 1 (SQLite is
 * deferred to Phase 4's history feature — no need for it before then).
 * Writes are atomic (write to a temp file, then rename) so a crash
 * mid-write can never leave a corrupt store on disk; a corrupt/unreadable
 * existing file is treated as empty rather than crashing the gateway.
 */
export class DeviceStore {
  #path: string;
  #logger: Logger;
  #devices: Map<string, DeviceRecord> = new Map();

  constructor(path: string, logger: Logger) {
    this.#path = path;
    this.#logger = logger;
  }

  async load(): Promise<void> {
    let raw: string;
    try {
      raw = await readFile(this.#path, "utf-8");
    } catch {
      this.#devices = new Map();
      return;
    }
    try {
      const parsed = StoreFileSchema.parse(JSON.parse(raw));
      this.#devices = new Map(Object.entries(parsed));
    } catch (err) {
      this.#logger.warn({ err }, "device store file is corrupt or invalid; starting empty");
      this.#devices = new Map();
    }
  }

  private async persist(): Promise<void> {
    const obj: StoreFile = Object.fromEntries(this.#devices);
    const dir = dirname(this.#path);
    await mkdir(dir, { recursive: true });
    const tmpPath = `${this.#path}.tmp`;
    await writeFile(tmpPath, JSON.stringify(obj, null, 2), "utf-8");
    await rename(tmpPath, this.#path);
  }

  get(deviceId: string): DeviceRecord | undefined {
    return this.#devices.get(deviceId);
  }

  getAll(): DeviceRecord[] {
    return [...this.#devices.values()];
  }

  async upsertConfig(deviceId: string, config: DeviceConfiguration): Promise<void> {
    const existing = this.#devices.get(deviceId);
    if (!existing) {
      throw new Error(`Cannot update config for unclaimed device ${deviceId}`);
    }
    this.#devices.set(deviceId, { ...existing, config });
    await this.persist();
  }

  async claim(deviceId: string, pairingToken: string, config: DeviceConfiguration): Promise<void> {
    this.#devices.set(deviceId, { config, pairingToken });
    await this.persist();
  }

  async touchLastSeen(deviceId: string, at: Date): Promise<void> {
    const existing = this.#devices.get(deviceId);
    if (!existing) return;
    this.#devices.set(deviceId, { ...existing, lastSeenAt: at.toISOString() });
    await this.persist();
  }

  isValidToken(deviceId: string, token: string): boolean {
    return this.#devices.get(deviceId)?.pairingToken === token;
  }
}
