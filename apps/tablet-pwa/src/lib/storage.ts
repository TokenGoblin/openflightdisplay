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

/**
 * In-progress setup-wizard state. Verified needed on real hardware: a
 * mobile browser reloaded the PWA's tab mid-wizard (after the user
 * switched tabs to look up a lat/lon), which reset all in-memory
 * component state -- including the fact that pairing with the Core2 had
 * already succeeded -- with no way to recover short of re-pairing from
 * scratch. Persisting each step's progress lets the wizard resume where
 * it left off instead.
 */
const WizardProgressSchema = z.object({
  step: z.enum(["pair", "location", "radius", "confirm"]),
  draft: z.object({
    core2BaseUrl: z.string(),
    code: z.string(),
    gatewayBaseUrl: z.string(),
    deviceId: z.string(),
    deviceName: z.string(),
    pairingToken: z.string(),
    latitude: z.number(),
    longitude: z.number(),
    radiusKm: z.number(),
  }),
});
export type WizardProgress = z.infer<typeof WizardProgressSchema>;

const WIZARD_PROGRESS_KEY = "openflightdisplay.wizard-progress.v1";

export function loadWizardProgress(): WizardProgress | null {
  const raw = window.localStorage.getItem(WIZARD_PROGRESS_KEY);
  if (!raw) return null;
  try {
    return WizardProgressSchema.parse(JSON.parse(raw));
  } catch {
    return null;
  }
}

export function saveWizardProgress(progress: WizardProgress): void {
  window.localStorage.setItem(WIZARD_PROGRESS_KEY, JSON.stringify(progress));
}

export function clearWizardProgress(): void {
  window.localStorage.removeItem(WIZARD_PROGRESS_KEY);
}
