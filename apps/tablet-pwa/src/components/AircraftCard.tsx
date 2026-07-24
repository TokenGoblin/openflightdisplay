import type { AircraftState } from "@openflightdisplay/shared-models";

function formatAge(lastUpdatedAt: Date | null): string {
  if (!lastUpdatedAt) return "—";
  const seconds = Math.max(0, Math.round((Date.now() - lastUpdatedAt.getTime()) / 1000));
  return `${seconds}s ago`;
}

export function AircraftCard({ aircraft, lastUpdatedAt }: { aircraft: AircraftState; lastUpdatedAt: Date | null }) {
  return (
    <section
      aria-label="Nearest aircraft"
      style={{
        background: "#1c2740",
        color: "#eef3fb",
        borderRadius: 12,
        padding: "1rem 1.25rem",
        minWidth: 260,
      }}
    >
      <h2 style={{ margin: 0, fontSize: "1.5rem" }}>{aircraft.callsign ?? aircraft.icaoHex}</h2>
      {aircraft.aircraftTypeCode ? <p style={{ margin: "0.25rem 0", color: "#9fb0c8" }}>{aircraft.aircraftTypeCode}</p> : null}

      <dl style={{ display: "grid", gridTemplateColumns: "auto 1fr", gap: "0.25rem 0.75rem", margin: "0.75rem 0" }}>
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

      <p style={{ margin: 0, fontSize: "0.85rem", color: "#9fb0c8" }}>Updated {formatAge(lastUpdatedAt)}</p>
    </section>
  );
}
