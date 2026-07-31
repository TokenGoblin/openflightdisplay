import { useState } from "react";
import { useAircraftFeed } from "../hooks/useAircraftFeed";
import { StatusBanner } from "../components/StatusBanner";
import { AircraftCard } from "../components/AircraftCard";
import { AircraftMap } from "../components/AircraftMap";
import { TrackFlightPanel } from "../components/TrackFlightPanel";
import { useDeviceStatus } from "../hooks/useDeviceStatus";
import { deriveStatus } from "../lib/status";
import type { StoredConnection } from "../lib/storage";
import { clearStoredConnection } from "../lib/storage";

export function DisplayPage({ connection, onReconfigure }: { connection: StoredConnection; onReconfigure: () => void }) {
  const feed = useAircraftFeed(connection.gatewayBaseUrl, connection.deviceId, connection.pairingToken);
  const status = deriveStatus(feed, true);
  const [isKiosk, setIsKiosk] = useState(false);
  const nearest = feed.aircraft[0];
  // Flight tracking is a conversation with the device itself, not the
  // gateway -- the device does the callsign polling and owns the ETA.
  // Only available when we know the device's own LAN address, which is
  // optional on a stored connection (a PWA paired against a gateway
  // alone has no device to ask).
  const device = useDeviceStatus(connection.core2BaseUrl);

  // Phase 1 only implements a basic circle area preview centered on the
  // first aircraft's observer reference isn't available client-side, so
  // the map centers on the aircraft list's own coordinates when present;
  // full monitoring-area display on the tablet is refined in Phase 2.
  const fallbackArea = { kind: "circle" as const, centerLat: nearest?.latitude ?? 0, centerLon: nearest?.longitude ?? 0, radiusKm: 15 };

  return (
    // Verified needed on real hardware: 100vh on mobile Safari/Chrome
    // includes the area covered by the browser's collapsible address
    // bar/toolbar, so content sized to exactly 100vh renders mostly
    // below the visible fold. 100dvh (dynamic viewport height) tracks
    // the actually-visible area instead.
    <div className="display-page">
      <StatusBanner status={status} detail={feed.providerStatus?.message} />
      {!isKiosk ? (
        <header className="display-page__header">
          <strong className="display-page__header-name">{connection.deviceName}</strong>
          <div className="display-page__header-actions">
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
      {/* minHeight: 0 is required here -- without it, a flex item inside
          a column flex container defaults to min-height: auto, which
          lets it grow to fit its content (Leaflet's map) instead of
          shrinking to the space flex:1 actually allotted it, pushing
          the map's true rendered size past the visible area. */}
      <div className="display-page__content">
        <div className="display-page__map-area">
          <AircraftMap area={fallbackArea} aircraft={feed.aircraft} />
        </div>
        <div className="display-page__sidebar">
          {nearest ? <AircraftCard aircraft={nearest} lastUpdatedAt={feed.lastUpdatedAt} /> : null}
          {/* Hidden in kiosk mode: that's a display surface, not a
              control surface, and a text input in it invites taps that
              were meant for the map. */}
          {connection.core2BaseUrl && !isKiosk ? (
            <TrackFlightPanel
              core2BaseUrl={connection.core2BaseUrl}
              pairingToken={connection.pairingToken}
              status={device.status?.trackedFlight}
            />
          ) : null}
        </div>
      </div>
      {isKiosk ? (
        <button type="button" className="display-page__kiosk-exit" onClick={() => setIsKiosk(false)}>
          Exit full screen
        </button>
      ) : null}
    </div>
  );
}