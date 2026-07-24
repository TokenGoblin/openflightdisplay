import { beforeEach, describe, expect, it } from "vitest";
import { clearStoredConnection, loadStoredConnection, saveStoredConnection } from "../src/lib/storage";

beforeEach(() => {
  window.localStorage.clear();
});

describe("stored connection", () => {
  it("returns null when nothing has been saved", () => {
    expect(loadStoredConnection()).toBeNull();
  });

  it("round-trips a saved connection", () => {
    const connection = {
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      gatewayBaseUrl: "http://192.168.1.50:8787",
      pairingToken: "tok-1",
    };
    saveStoredConnection(connection);
    expect(loadStoredConnection()).toEqual(connection);
  });

  it("never persists anything under a wifi-credential-shaped key", () => {
    saveStoredConnection({
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      gatewayBaseUrl: "http://192.168.1.50:8787",
      pairingToken: "tok-1",
    });
    const rawKeys = Object.keys(window.localStorage);
    for (const key of rawKeys) {
      expect(key.toLowerCase()).not.toContain("wifi");
      expect(window.localStorage.getItem(key)?.toLowerCase()).not.toContain("password");
    }
  });

  it("treats a corrupt stored value as not-configured rather than throwing", () => {
    window.localStorage.setItem("openflightdisplay.connection.v1", "{ not valid json");
    expect(loadStoredConnection()).toBeNull();
  });

  it("clears the stored connection", () => {
    saveStoredConnection({
      deviceId: "core2-abc123",
      deviceName: "Living Room",
      gatewayBaseUrl: "http://192.168.1.50:8787",
      pairingToken: "tok-1",
    });
    clearStoredConnection();
    expect(loadStoredConnection()).toBeNull();
  });
});
