import { useEffect, useState } from "react";
import type { Core2StatusResponse } from "@openflightdisplay/protocol";
import { getCore2Status } from "../lib/api";

const POLL_INTERVAL_MS = 15_000;

/**
 * Polls a paired device's own status endpoint.
 *
 * Deliberately reads tracked-flight state from the *device* rather than
 * recomputing it here. The device is the thing doing the adaptive
 * callsign polling, so it always has the fresher position — and if the
 * tablet computed its own ETA from a different snapshot, the panel and
 * the physical display would show two different arrival times for the
 * same flight. One of them would be wrong, and there would be no way to
 * tell which.
 *
 * A failed poll leaves the previous status in place rather than blanking
 * it: a display that briefly can't be reached is a much better
 * explanation than a countdown that vanishes.
 */
export function useDeviceStatus(core2BaseUrl: string | undefined) {
  const [status, setStatus] = useState<Core2StatusResponse | null>(null);
  const [reachable, setReachable] = useState(true);

  useEffect(() => {
    if (!core2BaseUrl) {
      setStatus(null);
      return;
    }

    let cancelled = false;

    async function poll() {
      try {
        const next = await getCore2Status(core2BaseUrl!);
        if (cancelled) return;
        setStatus(next);
        setReachable(true);
      } catch {
        if (!cancelled) setReachable(false);
      }
    }

    void poll();
    const timer = window.setInterval(() => void poll(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [core2BaseUrl]);

  return { status, reachable };
}
