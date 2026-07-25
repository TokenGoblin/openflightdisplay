import type { AircraftState } from "@openflightdisplay/shared-models";

function formatAge(lastUpdatedAt: Date | null): string {
  if (!lastUpdatedAt) return "—";
  const seconds = Math.max(0, Math.round((Date.now() - lastUpdatedAt.getTime()) / 1000));
  return `${seconds}s ago`;
}

export function AircraftCard({ aircraft, lastUpdatedAt }: { aircraft: AircraftState; lastUpdatedAt: Date | null }) {
  return (
    <section aria-label="Nearest aircraft" className="aircraft-card">
      <h2 className="aircraft-card__heading">{aircraft.callsign ?? aircraft.icaoHex}</h2>
      {aircraft.aircraftTypeCode ? <p className="aircraft-card__type">{aircraft.aircraftTypeCode}</p> : null}

      <dl className="aircraft-card__details">
        {aircraft.distanceFromObserverKm !== undefined ? (
          <>
            <dt>Distance</dt>
            <dd>{aircraft.distanceFromObserverKm.toFixed(1)} km</dd>
          </>
        ) : null}
        {aircraft.bearingFromObserverDeg !== undefined ? (
          <>
            <dt>Bearing</dt>
            <dd>{Math.round(aircraft.bearingFromObserverDeg)}°</dd>
          </>
        ) : null}
        {aircraft.geometricAltitudeFt !== undefined || aircraft.barometricAltitudeFt !== undefined ? (
          <>
            <dt>Altitude</dt>
            <dd>{Math.round((aircraft.geometricAltitudeFt ?? aircraft.barometricAltitudeFt)!)} ft</dd>
          </>
        ) : null}
        {aircraft.groundSpeedKt !== undefined ? (
          <>
            <dt>Speed</dt>
            <dd>{Math.round(aircraft.groundSpeedKt)} kt</dd>
          </>
        ) : null}
        {aircraft.trackHeadingDeg !== undefined ? (
          <>
            <dt>Heading</dt>
            <dd>{Math.round(aircraft.trackHeadingDeg)}°</dd>
          </>
        ) : null}
      </dl>

      <p className="aircraft-card__age">Updated {formatAge(lastUpdatedAt)}</p>
    </section>
  );
}