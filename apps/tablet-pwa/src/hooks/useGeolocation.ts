import { useCallback, useState } from "react";

export interface GeolocationResult {
  latitude: number;
  longitude: number;
}

/**
 * Deliberately does NOT request geolocation on mount -- only when the
 * user explicitly calls `request()` (e.g. clicking "Use my location"),
 * per docs/SECURITY_AND_PRIVACY.md's "explicit permission before
 * geolocation" requirement.
 */
export function useGeolocation() {
  const [result, setResult] = useState<GeolocationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);

  const request = useCallback(() => {
    if (!("geolocation" in navigator)) {
      setError("Geolocation is not available in this browser");
      return;
    }
    setIsLoading(true);
    setError(null);
    navigator.geolocation.getCurrentPosition(
      (position) => {
        setResult({ latitude: position.coords.latitude, longitude: position.coords.longitude });
        setIsLoading(false);
      },
      (err) => {
        setError(err.message || "Could not determine your location");
        setIsLoading(false);
      },
      { enableHighAccuracy: false, timeout: 10_000 },
    );
  }, []);

  return { result, error, isLoading, request };
}
