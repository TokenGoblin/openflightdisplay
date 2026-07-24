import type { CircleMonitoringArea } from "@openflightdisplay/shared-models";
import type { EmergencyState, AircraftCategory } from "@openflightdisplay/shared-models";
import { ProviderFetchError, type AviationDataProvider, type RawProviderAircraft } from "./provider.js";

const KM_TO_NM = 1 / 1.852;
const FETCH_TIMEOUT_MS = 8_000;

/**
 * adsb.lol adapter. Base URL and no-API-key-required status confirmed via
 * docs.adsb.lol at review time (see docs/DATA_SOURCE_EVALUATION.md), but
 * the exact point/radius path below follows the common tar1090-derived
 * "aircraft.json"-style convention shared across adsb.lol/adsb.fi/
 * airplanes.live forks -- RE-VERIFY against https://api.adsb.lol/docs
 * before relying on this in production; it was not independently
 * confirmed live in this session.
 */
export class AdsbLolProvider implements AviationDataProvider {
  readonly id = "adsblol";
  readonly requiresApiKey = false;
  readonly pollIntervalMs = 15_000;

  #baseUrl: string;

  constructor(baseUrl = "https://api.adsb.lol") {
    this.#baseUrl = baseUrl;
  }

  async fetchAircraft(area: CircleMonitoringArea): Promise<RawProviderAircraft[]> {
    const radiusNm = Math.min(area.radiusKm * KM_TO_NM, 250);
    const url = `${this.#baseUrl}/v2/point/${area.centerLat}/${area.centerLon}/${radiusNm.toFixed(1)}`;

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
    let response: Response;
    try {
      response = await fetch(url, { signal: controller.signal });
    } catch (err) {
      throw new ProviderFetchError(this.id, `request to adsb.lol failed: ${String(err)}`, err);
    } finally {
      clearTimeout(timeout);
    }

    if (!response.ok) {
      throw new ProviderFetchError(this.id, `adsb.lol returned HTTP ${response.status}`);
    }

    let body: unknown;
    try {
      body = await response.json();
    } catch (err) {
      throw new ProviderFetchError(this.id, `adsb.lol returned invalid JSON`, err);
    }

    const list = extractAircraftArray(body);
    const now = new Date().toISOString();
    return list.map((raw) => normalizeAdsbLolAircraft(raw, this.id, now)).filter((a): a is RawProviderAircraft => a !== null);
  }
}

function extractAircraftArray(body: unknown): unknown[] {
  if (body && typeof body === "object" && "ac" in body && Array.isArray((body as { ac: unknown }).ac)) {
    return (body as { ac: unknown[] }).ac;
  }
  return [];
}

function mapCategory(raw: string | undefined): AircraftCategory | undefined {
  if (!raw) return undefined;
  if (raw.startsWith("A")) return "fixed-wing";
  if (raw === "B2") return "balloon";
  if (raw === "B6") return "drone";
  if (raw === "B7") return "rotorcraft";
  if (raw === "B1") return "glider";
  if (raw.startsWith("C")) return "ground-vehicle";
  return "unknown";
}

function mapEmergency(raw: string | undefined): EmergencyState {
  switch (raw) {
    case "general":
      return "general";
    case "lifeguard":
      return "medical";
    case "minfuel":
      return "minimum-fuel";
    case "nordo":
      return "no-communications";
    case "unlawful":
      return "unlawful-interference";
    case "downed":
      return "downed";
    default:
      return "none";
  }
}

/** Best-effort mapping from adsb.lol's tar1090-style aircraft record to AircraftState. */
function normalizeAdsbLolAircraft(
  raw: unknown,
  providerId: string,
  now: string,
): RawProviderAircraft | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  const hex = typeof r.hex === "string" ? r.hex.replace(/^~/, "").toLowerCase() : undefined;
  const lat = typeof r.lat === "number" ? r.lat : undefined;
  const lon = typeof r.lon === "number" ? r.lon : undefined;
  if (!hex || !/^[0-9a-f]{6}$/.test(hex) || lat === undefined || lon === undefined) {
    // No usable position or identity -- drop rather than emit a garbage record.
    return null;
  }

  const onGround = r.alt_baro === "ground";
  const dataQualityFlags: RawProviderAircraft["dataQualityFlags"] = [];
  if (!r.flight) dataQualityFlags.push("no-callsign");
  if (onGround || r.alt_baro === undefined) dataQualityFlags.push("no-altitude");

  return {
    provider: providerId,
    icaoHex: hex,
    callsign: typeof r.flight === "string" ? r.flight.trim() : undefined,
    registration: typeof r.r === "string" ? r.r : undefined,
    aircraftTypeCode: typeof r.t === "string" ? r.t : undefined,
    aircraftCategory: mapCategory(typeof r.category === "string" ? r.category : undefined),
    latitude: lat,
    longitude: lon,
    barometricAltitudeFt: typeof r.alt_baro === "number" ? r.alt_baro : undefined,
    geometricAltitudeFt: typeof r.alt_geom === "number" ? r.alt_geom : undefined,
    groundSpeedKt: typeof r.gs === "number" ? r.gs : undefined,
    trackHeadingDeg: typeof r.track === "number" ? r.track : undefined,
    verticalRateFtPerMin:
      typeof r.baro_rate === "number" ? r.baro_rate : typeof r.geom_rate === "number" ? r.geom_rate : undefined,
    squawk: typeof r.squawk === "string" && /^[0-7]{4}$/.test(r.squawk) ? r.squawk : undefined,
    emergencyState: mapEmergency(typeof r.emergency === "string" ? r.emergency : undefined),
    onGround,
    firstSeen: now,
    lastSeen: now,
    positionTimestamp: now,
    dataQualityFlags,
  };
}
