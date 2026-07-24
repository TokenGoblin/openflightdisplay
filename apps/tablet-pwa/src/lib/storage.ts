import { z } from "zod";

/**
 * Everything persisted here is LAN connection info and a pairing token
 * -- never Wi-Fi credentials (those are entered once, directly into the
 * Core2's own captive portal, and never touch the tablet). See
 * docs/SECURITY_AND_PRIVACY.md.
 */
const StoredConnectionSchema = z.object({
  deviceId: z.string().min(1),
  deviceName: z.string().min(1),
  core2BaseUrl: z.string().url().optional(),
  gatewayBaseUrl: z.string().url(),
  pairingToken: z.string().min(1),
});
export type StoredConnection = z.infer<typeof StoredConnectionSchema>;

const STORAGE_KEY = "openflightdisplay.connection.v1";

export function loadStoredConnection(): StoredConnection | null {
  const raw = window.localStorage.getItem(STORAGE_KEY);
  if (!raw) return null;
  try {
    return StoredConnectionSchema.parse(JSON.parse(raw));
  } catch {
    // Corrupt/outdated stored value -- treat as "not configured yet"
    // rather than crash the app on load.
    return null;
  }
}

export function saveStoredConnection(connection: StoredConnection): void {
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(connection));
}

export function clearStoredConnection(): void {
  window.localStorage.removeItem(STORAGE_KEY);
}
