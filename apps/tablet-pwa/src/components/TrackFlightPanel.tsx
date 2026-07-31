import { useState } from "react";
import type { TrackedFlightStatus } from "@openflightdisplay/protocol";
import { TrackedFlightSchema } from "@openflightdisplay/shared-models";
import { ApiError, putTrackedFlight } from "../lib/api";

/**
 * Start or stop following one flight to its destination — the "when do I
 * leave to collect someone" panel.
 *
 * Two things here are deliberate and worth not undoing:
 *
 * 1. The flight number is sent exactly as typed. The *device* translates
 *    IATA to the ICAO callsign ADS-B broadcasts ("UA1234" → "UAL1234"),
 *    because it already carries the airline table for decoding callsigns.
 *    Duplicating that table here would create a second source of truth
 *    that could silently disagree with the firmware.
 *
 * 2. Nothing in this component computes an ETA. Every number shown comes
 *    from the device's own status endpoint, so the panel and the physical
 *    display can never contradict each other — which they would, given
 *    they'd be polling on different schedules.
 */

/** What the device is telling us, phrased for someone deciding when to drive. */
function phaseDescription(status: TrackedFlightStatus): string {
  switch (status.phase) {
    case "WAITING":
      return status.destinationResolved
        ? "Not transmitting yet — normal before pushback."
        : "That destination airport wasn't recognised.";
    case "ENROUTE":
      return "In the air and en route.";
    case "DESCENDING":
      return "Descending.";
    case "APPROACHING":
      return "On approach — time to go.";
    case "LANDED":
      return "Landed.";
    case "NO CONTACT":
      return "Signal lost — this happens over water and in coverage gaps. Not a landing.";
  }
}

function formatEta(status: TrackedFlightStatus): string {
  if (status.minutesRemaining === undefined) return "—";
  const mins = status.minutesRemaining;
  if (mins < 60) return `${mins} min`;
  return `${Math.floor(mins / 60)}h ${String(mins % 60).padStart(2, "0")}m`;
}

export function TrackFlightPanel({
  core2BaseUrl,
  pairingToken,
  status,
}: {
  core2BaseUrl: string;
  pairingToken: string;
  // Explicitly `| undefined` rather than just optional: the project
  // builds with exactOptionalPropertyTypes, under which "may be absent"
  // and "may be undefined" are different types, and the caller passes a
  // value that may genuinely be undefined.
  status?: TrackedFlightStatus | undefined;
}) {
  const [flight, setFlight] = useState("");
  const [destinationIcao, setDestinationIcao] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setError(null);

    // Validated against the shared schema before any request, so a typo
    // produces an immediate, specific message instead of a round trip
    // ending in a generic 400.
    const parsed = TrackedFlightSchema.safeParse({
      flight: flight.trim(),
      destinationIcao: destinationIcao.trim().toUpperCase(),
    });
    if (!parsed.success) {
      setError(parsed.error.issues[0]?.message ?? "Check the flight number and destination.");
      return;
    }

    setBusy(true);
    try {
      await putTrackedFlight(core2BaseUrl, pairingToken, parsed.data);
      setFlight("");
      setDestinationIcao("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not reach the display.");
    } finally {
      setBusy(false);
    }
  }

  async function stop() {
    setError(null);
    setBusy(true);
    try {
      await putTrackedFlight(core2BaseUrl, pairingToken, null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not reach the display.");
    } finally {
      setBusy(false);
    }
  }

  if (status) {
    return (
      <section aria-label="Tracked flight" className="track-panel">
        <h2 className="track-panel__heading">{status.flight}</h2>
        <p className="track-panel__phase">{phaseDescription(status)}</p>

        <dl className="track-panel__details">
          <dt>Arrives in</dt>
          <dd>{formatEta(status)}</dd>
          <dt>Destination</dt>
          <dd>{status.destinationIcao}</dd>
          {status.distanceToDestinationNm !== undefined ? (
            <>
              <dt>To go</dt>
              <dd>{Math.round(status.distanceToDestinationNm)} NM</dd>
            </>
          ) : null}
          <dt>Status</dt>
          <dd>{status.phase}</dd>
        </dl>

        {/* Said plainly rather than buried: this is a projection from the
            aircraft's current speed, not a published arrival time. */}
        <p className="track-panel__caveat">
          Estimated from the aircraft&rsquo;s current position and groundspeed. This is not a scheduled
          arrival time, and it does not include taxi time to the gate.
        </p>

        <button type="button" onClick={stop} disabled={busy}>
          {busy ? "Stopping…" : "Stop tracking"}
        </button>
        {error ? <p role="alert">{error}</p> : null}
      </section>
    );
  }

  return (
    <section aria-label="Track a flight" className="track-panel">
      <h2 className="track-panel__heading">Track a flight</h2>
      <form onSubmit={submit}>
        <label>
          Flight number
          <input
            value={flight}
            onChange={(e) => setFlight(e.target.value)}
            placeholder="UA1234"
            maxLength={11}
            required
          />
        </label>
        <label>
          Arrival airport (ICAO)
          <input
            value={destinationIcao}
            onChange={(e) => setDestinationIcao(e.target.value)}
            placeholder="KSEA"
            maxLength={4}
            required
          />
        </label>
        {/* ICAO rather than IATA is a hard requirement, not a preference:
            the airport lookup returns null for IATA codes, and "SEA" →
            "KSEA" only holds in North America. Saying so up front beats a
            rejection after submit. */}
        <p className="track-panel__hint">
          Four-letter ICAO code — KSEA, not SEA. EGLL for Heathrow.
        </p>
        <button type="submit" disabled={busy}>
          {busy ? "Starting…" : "Start tracking"}
        </button>
      </form>
      {error ? <p role="alert">{error}</p> : null}
    </section>
  );
}
