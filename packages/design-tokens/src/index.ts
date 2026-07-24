/**
 * Branding is deliberately isolated here so the project can be renamed
 * later without restructuring the codebase (per docs/ATTRIBUTION.md — do
 * not use "FlightWall" or confusingly similar names/marks anywhere).
 */
export const BRAND = {
  productName: "OpenFlightDisplay",
  shortName: "OFD",
  tagline: "See what's flying overhead",
} as const;

/**
 * Original, aviation-inspired palette (dark cockpit-instrument aesthetic).
 * Not copied from any commercial product's visual design. Kept small and
 * legible-first for a 320x240 glanceable display and readable in daylight
 * on a tablet.
 */
export const COLOR = {
  backgroundPrimary: "#0b1220",
  backgroundSecondary: "#141d2e",
  surface: "#1c2740",
  textPrimary: "#eef3fb",
  textSecondary: "#9fb0c8",
  accentAmber: "#f5a623",
  accentCyan: "#3ecfd8",
  statusOk: "#3ecf7f",
  statusDegraded: "#f5a623",
  statusUnavailable: "#e5484d",
  statusStale: "#8a93a6",
} as const;

export const SPACING_PX = {
  xs: 4,
  sm: 8,
  md: 16,
  lg: 24,
  xl: 32,
} as const;

export const FONT_SCALE = {
  core2Compact: 1,
  core2Default: 1.25,
  core2Large: 1.6,
} as const;

export type ColorToken = keyof typeof COLOR;
