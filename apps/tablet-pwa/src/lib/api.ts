import {
  Core2StatusResponseSchema,
  GatewayStatusResponseSchema,
  PairClaimResponseSchema,
  CURRENT_SCHEMA_VERSION,
  type Core2StatusResponse,
  type GatewayStatusResponse,
} from "@openflightdisplay/protocol";
import type { DeviceConfiguration } from "@openflightdisplay/shared-models";
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
    (body) => DeviceConfigurationSchema.parse((body as { config: unknown }).config),
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
