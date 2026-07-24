import type { AircraftFeedState } from "../hooks/useAircraftFeed";

/**
 * Mirrors firmware/core2/src/main.cpp's renderCurrentState() decision
 * order, so the tablet and Core2 agree on what "no data yet" vs. "data
 * source down" vs. "stale" mean -- see docs/PRODUCT_REQUIREMENTS.md's
 * requirement that data age/errors are always explicit, never an
 * indefinite loading state.
 */
export type StatusKind =
  | "configuration-required"
  | "connecting"
  | "data-source-unavailable"
  | "waiting-for-first-data"
  | "no-matching-aircraft"
  | "stale"
  | "showing-aircraft";

const STALE_THRESHOLD_MS = 60_000;

export function deriveStatus(feed: AircraftFeedState, hasConfig: boolean, now: Date = new Date()): StatusKind {
  if (!hasConfig) return "configuration-required";
  if (feed.connectionState !== "connected") return "connecting";
  if (feed.providerStatus?.status === "unavailable") return "data-source-unavailable";
  if (!feed.lastUpdatedAt) return "waiting-for-first-data";
  if (feed.aircraft.length === 0) return "no-matching-aircraft";
  if (now.getTime() - feed.lastUpdatedAt.getTime() > STALE_THRESHOLD_MS) return "stale";
  return "showing-aircraft";
}

export const STATUS_LABEL: Record<StatusKind, string> = {
  "configuration-required": "Configuration required",
  connecting: "Connecting to gateway…",
  "data-source-unavailable": "Data source unavailable",
  "waiting-for-first-data": "Waiting for first data",
  "no-matching-aircraft": "No matching aircraft",
  stale: "Data is stale",
  "showing-aircraft": "",
};
