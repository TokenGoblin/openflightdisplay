import { z } from "zod";

/**
 * One flight the user has asked a device to follow to its destination —
 * the "am I leaving for the airport at the right time" case.
 *
 * Mirrored in firmware by `ofd::TrackedFlightConfig`
 * (firmware/display/include/domain/config.h); `docs/PROTOCOL.md` is the
 * contract of record when the two disagree.
 *
 * Two things about this shape are load-bearing:
 *
 * 1. `flight` is whatever the user typed — a boarding-pass flight number
 *    ("UA1234") or a raw ADS-B callsign ("UAL1234"). The *device* does
 *    the IATA→ICAO translation, because it already carries the airline
 *    table for decoding callsigns; duplicating that table in TypeScript
 *    would create two sources of truth that could silently disagree.
 *    `callsign` is the normalized result, returned on read and ignored
 *    on write.
 *
 * 2. `destinationIcao` is required and must be ICAO, not IATA. ADS-B
 *    carries no destination — adsb.lol's route-inference endpoint returns
 *    an empty 201 — so the user supplies it, and the airport lookup that
 *    resolves it to coordinates answers `null` for IATA codes. There is
 *    no safe generic way to expand "SEA" into "KSEA" (the K prefix is
 *    North America only), so it is rejected rather than guessed.
 */
export const TrackedFlightSchema = z.object({
  /** Flight number or callsign as entered by the user, e.g. "UA1234". */
  flight: z.string().min(3).max(11),
  /**
   * Normalized ADS-B callsign, e.g. "UAL1234". Device-derived: present
   * in responses, ignored in requests.
   */
  callsign: z.string().min(3).max(11).optional(),
  /** Arrival airport, 4-letter ICAO (e.g. "KSEA"). */
  destinationIcao: z
    .string()
    .regex(/^[A-Za-z]{4}$/, "destinationIcao must be a 4-letter ICAO code (e.g. KSEA, not SEA)"),
  /**
   * Door-to-arrivals-hall travel time, in minutes. Omitted or 0 disables
   * the leave-now advice entirely rather than guessing a number.
   */
  travelMinutes: z.number().int().min(0).max(720).optional(),
  /**
   * Estimated minutes between touchdown and the person actually walking
   * out — taxi, deplaning, immigration, bags.
   *
   * This exists because leaving it out is the obvious way to get this
   * feature wrong: an alert keyed to touchdown alone sends people to the
   * airport 20–45 minutes early, which is the exact problem the feature
   * is meant to solve. Defaulted rather than required, on the grounds
   * that a stated default is easier to correct than a hidden assumption.
   */
  postLandingMinutes: z.number().int().min(0).max(240).default(30),
});
export type TrackedFlight = z.infer<typeof TrackedFlightSchema>;
