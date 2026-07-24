import { useState } from "react";
import { useAircraftFeed } from "../hooks/useAircraftFeed";
import { StatusBanner } from "../components/StatusBanner";
import { AircraftCard } from "../components/AircraftCard";
import { AircraftMap } from "../components/AircraftMap";
import { deriveStatus } from "../lib/status";
import type { StoredConnection } from "../lib/storage";
import { clearStoredConnection } from "../lib/storage";

export function DisplayPage({ connection, onReconfigure }: { connection: StoredConnection; onReconfigure: () => void }) {
  const feed = useAircraftFeed(connection.gatewayBaseUrl, connection.deviceId, connection.pairingToken);
  const status = deriveStatus(feed, true);
  const [isKiosk, setIsKiosk] = useState(false);
  const nearest = feed.aircraft[0];

  // Phase 1 only implements a basic circle area preview centered on the
  // first aircraft's observer reference isn't available client-side, so
  // the map centers on the aircraft list's own coordinates when present;
  // full monitoring-area display on the tablet is refined in Phase 2.
  const fallbackArea = { kind: "circle" as const, centerLat: nearest?.latitude ?? 0, centerLon: nearest?.longitude ?? 0, radiusKm: 15 };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh" }}>
      <StatusBanner status={status} detail={feed.providerStatus?.message} />
      {!isKiosk ? (
        <header style={{ display: "flex", justifyContent: "space-between", padding: "0.5rem 1rem" }}>
          <strong>{connection.deviceName}</strong>
          <div>
            <button type="button" onClick={() => setIsKiosk(true)}>
              Full screen
            </button>
            <button type="button" onClick={onReconfigure}>
              Reconfigure
            </button>
            <button
              type="button"
              onClick={() => {
                clearStoredConnection();
                onReconfigure();
              }}
            >
              Remove display
            </button>
          </div>
        </header>
      ) : null}
      <div style={{ flex: 1, display: "flex" }}>
        <div style={{ flex: 2 }}>
          <AircraftMap area={fallbackArea} aircraft={feed.aircraft} />
        </div>
        <div style={{ flex: 1, padding: "1rem" }}>{nearest ? <AircraftCard aircraft={nearest} lastUpdatedAt={feed.lastUpdatedAt} /> : null}</div>
      </div>
      {isKiosk ? (
        <button type="button" style={{ position: "fixed", bottom: 8, right: 8 }} onClick={() => setIsKiosk(false)}>
          Exit full screen
        </button>
      ) : null}
    </div>
  );
}
