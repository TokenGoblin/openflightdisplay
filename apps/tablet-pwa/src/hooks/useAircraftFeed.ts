import { useEffect, useRef, useState } from "react";
import {
  ServerToClientMessageSchema,
  WS_DEAD_CONNECTION_TIMEOUT_MS,
  WS_RECONNECT_BASE_DELAY_MS,
  WS_RECONNECT_MAX_DELAY_MS,
} from "@openflightdisplay/protocol";
import type { AircraftState } from "@openflightdisplay/shared-models";

export type FeedConnectionState = "connecting" | "connected" | "disconnected";

export interface AircraftFeedState {
  connectionState: FeedConnectionState;
  aircraft: AircraftState[];
  lastUpdatedAt: Date | null;
  providerStatus: { provider: string; status: "ok" | "degraded" | "unavailable"; message?: string } | null;
}

function toWsUrl(gatewayBaseUrl: string, deviceId: string, pairingToken: string): string {
  const wsBase = gatewayBaseUrl.replace(/^http/, "ws");
  const params = new URLSearchParams({ deviceId, token: pairingToken });
  return `${wsBase}/ws/v1/aircraft?${params.toString()}`;
}

/**
 * Connects to the same gateway WebSocket feed the Core2 uses, so the
 * tablet shows the same aircraft (docs/ARCHITECTURE.md's "steady state
 * data flow"). Reconnects with exponential backoff + jitter and treats a
 * message-less connection as dead after WS_DEAD_CONNECTION_TIMEOUT_MS,
 * per docs/PROTOCOL.md.
 */
export function useAircraftFeed(
  gatewayBaseUrl: string | undefined,
  deviceId: string | undefined,
  pairingToken: string | undefined,
): AircraftFeedState {
  const [state, setState] = useState<AircraftFeedState>({
    connectionState: "connecting",
    aircraft: [],
    lastUpdatedAt: null,
    providerStatus: null,
  });

  const attemptRef = useRef(0);

  useEffect(() => {
    if (!gatewayBaseUrl || !deviceId || !pairingToken) {
      setState((s) => ({ ...s, connectionState: "disconnected" }));
      return;
    }

    let socket: WebSocket | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let deadCheckTimer: ReturnType<typeof setInterval> | null = null;
    let lastMessageAt = Date.now();
    let cancelled = false;

    function scheduleReconnect() {
      if (cancelled) return;
      const attempt = attemptRef.current++;
      const backoff = Math.min(WS_RECONNECT_BASE_DELAY_MS * 2 ** attempt, WS_RECONNECT_MAX_DELAY_MS);
      const jitter = Math.random() * backoff * 0.3;
      reconnectTimer = setTimeout(connect, backoff + jitter);
    }

    function connect() {
      if (cancelled) return;
      setState((s) => ({ ...s, connectionState: "connecting" }));
      socket = new WebSocket(toWsUrl(gatewayBaseUrl!, deviceId!, pairingToken!));

      socket.onopen = () => {
        attemptRef.current = 0;
        lastMessageAt = Date.now();
        setState((s) => ({ ...s, connectionState: "connected" }));
      };

      socket.onmessage = (event) => {
        lastMessageAt = Date.now();
        let parsed;
        try {
          parsed = ServerToClientMessageSchema.parse(JSON.parse(event.data));
        } catch {
          return; // malformed/unrecognized frame -- ignore, don't crash
        }
        if (parsed.type === "aircraft-update") {
          setState((s) => ({ ...s, aircraft: parsed.aircraft, lastUpdatedAt: new Date(parsed.generatedAt) }));
        } else if (parsed.type === "provider-status") {
          setState((s) => ({
            ...s,
            providerStatus: { provider: parsed.provider, status: parsed.status, message: parsed.message },
          }));
        }
        // heartbeat: no state change needed, lastMessageAt already bumped above
      };

      socket.onclose = () => {
        setState((s) => ({ ...s, connectionState: "disconnected" }));
        scheduleReconnect();
      };

      socket.onerror = () => {
        socket?.close();
      };
    }

    connect();
    deadCheckTimer = setInterval(() => {
      if (Date.now() - lastMessageAt > WS_DEAD_CONNECTION_TIMEOUT_MS) {
        socket?.close();
      }
    }, 5000);

    return () => {
      cancelled = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (deadCheckTimer) clearInterval(deadCheckTimer);
      socket?.close();
    };
  }, [gatewayBaseUrl, deviceId, pairingToken]);

  return state;
}
