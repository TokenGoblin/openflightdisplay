import {
  Core2StatusResponseSchema,
  GatewayStatusResponseSchema,
  PairClaimResponseSchema,
  CURRENT_SCHEMA_VERSION,
  type Core2StatusResponse,
  type GatewayStatusResponse,
} from "@openflightdisplay/protocol";
import type { DeviceConfiguration, TrackedFlight } from "@openflightdisplay/shared-models";
import { DeviceConfigurationSchema } from "@openflightdisplay/shared-models";

export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status?: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function requestJson<T>(url: string, init: RequestInit | undefined, parse: (body: unknown) => T): Promise<T> {
  let response: Response;
  try {
    response = await fetch(url, init);
  } catch (err) {
    throw new ApiError(`Network error reaching ${url}: ${String(err)}`);
  }
  if (!response.ok) {
    throw new ApiError(`Request to ${url} failed with HTTP ${response.status}`, response.status);
  }
  const body = await response.json();
  return parse(body);
}

/** Claims a Core2's pairing code (POST http://<core2Ip>/pair). */
export async function pairWithCore2(core2BaseUrl: string, code: string) {
  return requestJson(`${core2BaseUrl}/pair`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ schemaVersion: CURRENT_SCHEMA_VERSION, code }),
  }, (body) => PairClaimResponseSchema.parse(body));
}

export async function getCore2Status(core2BaseUrl: string): Promise<Core2StatusResponse> {
  return requestJson(`${core2BaseUrl}/api/v1/status`, undefined, (body) => Core2StatusResponseSchema.parse(body));
}

export async function putCore2Config(core2BaseUrl: string, pairingToken: string, config: DeviceConfiguration) {
  return requestJson(
    `${core2BaseUrl}/api/v1/config`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${pairingToken}` },
      body: JSON.stringify({ schemaVersion: CURRENT_SCHEMA_VERSION, config }),
    },
    // Verified needed on real hardware: unlike the gateway's REST routes
    // (which wrap responses as {schemaVersion, config}), the Core2's own
    // GET/PUT /api/v1/config returns the DeviceConfiguration fields
    // flat/bare -- matching docs/PROTOCOL.md and pairing_server.cpp's
    // serializeDeviceConfig(). Parsing it as {config: ...} here threw a
    // ZodError (not an ApiError), which showed up to the user as a
    // generic "Setup failed unexpectedly" with no useful detail.
    (body) => DeviceConfigurationSchema.parse(body),
  );
}

/**
 * Starts or stops following a flight, as a deliberately partial config
 * write: only `trackedFlight` is sent, so the device's monitoring area,
 * name and brightness are left exactly as they are. Pass `null` to stop.
 *
 * This relies on the device treating an absent key as "leave alone" and
 * an explicit null as "clear" — see docs/PROTOCOL.md. Sending a whole
 * DeviceConfiguration here instead would mean round-tripping every other
 * setting through the browser just to start a countdown, and would make
 * a stale local copy able to silently revert them.
 */
export async function putTrackedFlight(
  core2BaseUrl: string,
  pairingToken: string,
  trackedFlight: TrackedFlight | null,
) {
  return requestJson(
    `${core2BaseUrl}/api/v1/config`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${pairingToken}` },
      body: JSON.stringify({ schemaVersion: CURRENT_SCHEMA_VERSION, config: { trackedFlight } }),
    },
    (body) => DeviceConfigurationSchema.parse(body),
  );
}

/** Registers the same device+token with the gateway (POST /api/v1/devices/:id/claim). */
export async function claimDeviceWithGateway(
  gatewayBaseUrl: string,
  deviceId: string,
  deviceName: string,
  pairingToken: string,
) {
  return requestJson(
    `${gatewayBaseUrl}/api/v1/devices/${deviceId}/claim`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ schemaVersion: CURRENT_SCHEMA_VERSION, deviceId, deviceName, pairingToken }),
    },
    (body) => body,
  );
}

export async function putGatewayConfig(
  gatewayBaseUrl: string,
  deviceId: string,
  pairingToken: string,
  config: DeviceConfiguration,
) {
  return requestJson(
    `${gatewayBaseUrl}/api/v1/devices/${deviceId}/config`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${pairingToken}` },
      body: JSON.stringify({ schemaVersion: CURRENT_SCHEMA_VERSION, config }),
    },
    (body) => DeviceConfigurationSchema.parse((body as { config: unknown }).config),
  );
}

export async function getGatewayStatus(gatewayBaseUrl: string): Promise<GatewayStatusResponse> {
  return requestJson(`${gatewayBaseUrl}/api/v1/status`, undefined, (body) => GatewayStatusResponseSchema.parse(body));
}
