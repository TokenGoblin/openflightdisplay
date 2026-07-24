import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { DeviceStore } from "../src/lib/deviceStore.js";
import { createLogger } from "../src/lib/logger.js";

const logger = createLogger({ LOG_LEVEL: "fatal" });

let dir: string;
let storePath: string;

beforeEach(async () => {
  dir = await mkdtemp(join(tmpdir(), "ofd-device-store-"));
  storePath = join(dir, "devices.json");
});

afterEach(async () => {
  await rm(dir, { recursive: true, force: true });
});

describe("DeviceStore", () => {
  it("starts empty when no file exists", async () => {
    const store = new DeviceStore(storePath, logger);
    await store.load();
    expect(store.getAll()).toEqual([]);
  });

  it("treats a corrupt file as empty rather than throwing", async () => {
    await writeFile(storePath, "{ not valid json", "utf-8");
    const store = new DeviceStore(storePath, logger);
    await expect(store.load()).resolves.not.toThrow();
    expect(store.getAll()).toEqual([]);
  });

  it("claims a device, persists it, and round-trips through a new instance", async () => {
    const store = new DeviceStore(storePath, logger);
    await store.load();
    await store.claim("core2-abc123", "token-1", {
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
    });

    expect(store.isValidToken("core2-abc123", "token-1")).toBe(true);
    expect(store.isValidToken("core2-abc123", "wrong-token")).toBe(false);

    const reloaded = new DeviceStore(storePath, logger);
    await reloaded.load();
    expect(reloaded.get("core2-abc123")?.config.deviceName).toBe("Living Room");
  });

  it("rejects updating config for an unclaimed device", async () => {
    const store = new DeviceStore(storePath, logger);
    await store.load();
    await expect(
      store.upsertConfig("core2-never-claimed", {
        deviceId: "core2-never-claimed",
        deviceName: "X",
        displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
      }),
    ).rejects.toThrow();
  });

  it("survives a power-loss-style crash between temp-write and rename", async () => {
    // Simulate: a .tmp file was left behind from an interrupted write, but
    // the real file was never renamed into place. The store must load the
    // last-good file, not the stray tmp file.
    const store = new DeviceStore(storePath, logger);
    await store.load();
    await store.claim("core2-abc123", "token-1", {
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
    });
    await writeFile(`${storePath}.tmp`, "{ garbage", "utf-8");

    const reloaded = new DeviceStore(storePath, logger);
    await reloaded.load();
    expect(reloaded.get("core2-abc123")?.config.deviceName).toBe("Living Room");
  });

  it("survives a burst of concurrent writes without any of them rejecting", async () => {
    // Verified needed on real hardware: a burst of rapid WebSocket
    // reconnects fired several overlapping touchLastSeen() calls (no
    // await between them, matching how aircraftSocket.ts calls it from
    // each new connection), and two concurrent persist() calls raced on
    // the same temp file -- the second's rename() failed with ENOENT
    // because the first had already consumed it. That rejection was
    // unhandled at the fire-and-forget call site, which crashed the
    // entire gateway process (Node terminates on unhandled rejections by
    // default).
    const store = new DeviceStore(storePath, logger);
    await store.load();
    await store.claim("core2-abc123", "token-1", {
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
    });

    const burst = Array.from({ length: 20 }, (_, i) => store.touchLastSeen("core2-abc123", new Date(2026, 0, 1, 0, 0, i)));
    await expect(Promise.all(burst)).resolves.not.toThrow();

    const reloaded = new DeviceStore(storePath, logger);
    await reloaded.load();
    expect(reloaded.get("core2-abc123")?.lastSeenAt).toBeDefined();
  });
});
