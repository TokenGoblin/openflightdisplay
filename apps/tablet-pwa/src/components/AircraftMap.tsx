import { MapContainer, TileLayer, Marker, Popup, Circle } from "react-leaflet";
import L from "leaflet";
import "leaflet/dist/leaflet.css";
import markerIcon2x from "leaflet/dist/images/marker-icon-2x.png";
import markerIcon from "leaflet/dist/images/marker-icon.png";
import markerShadow from "leaflet/dist/images/marker-shadow.png";
import type { AircraftState, CircleMonitoringArea } from "@openflightdisplay/shared-models";

// Leaflet's default marker icon references relative image paths that
// don't resolve correctly under Vite's bundling -- this is the standard
// fix (see Leaflet/react-leaflet + bundler integration notes).
delete (L.Icon.Default.prototype as unknown as { _getIconUrl?: unknown })._getIconUrl;
L.Icon.Default.mergeOptions({
  iconRetinaUrl: markerIcon2x,
  iconUrl: markerIcon,
  shadowUrl: markerShadow,
});

/**
 * Phase 1 map: raster OpenStreetMap tiles via Leaflet (see
 * docs/ATTRIBUTION.md -- the default attribution control is left in
 * place, satisfying OSM's attribution requirement). MapLibre/vector
 * tiles, range rings, trails, and clustering are Phase 2/3
 * (docs/FEATURE_PARITY_MATRIX.md).
 */
export function AircraftMap({ area, aircraft }: { area: CircleMonitoringArea; aircraft: AircraftState[] }) {
  return (
    <MapContainer
      center={[area.centerLat, area.centerLon]}
      zoom={9}
      style={{ height: "100%", width: "100%" }}
      aria-label="Aircraft radar map"
    >
      <TileLayer
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
      />
      <Circle center={[area.centerLat, area.centerLon]} radius={area.radiusKm * 1000} pathOptions={{ color: "#3ecfd8", fillOpacity: 0.05 }} />
      {aircraft.map((a) => (
        <Marker key={a.icaoHex} position={[a.latitude, a.longitude]}>
          <Popup>
            <strong>{a.callsign ?? a.icaoHex}</strong>
            {a.geometricAltitudeFt !== undefined ? <div>{Math.round(a.geometricAltitudeFt)} ft</div> : null}
          </Popup>
        </Marker>
      ))}
    </MapContainer>
  );
}
