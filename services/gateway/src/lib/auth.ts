import type { FastifyReply, FastifyRequest } from "fastify";
import type { DeviceStore } from "./deviceStore.js";

/**
 * Extracts a Bearer token from the Authorization header and checks it
 * against the given device's stored pairing token. Sends a 401 and
 * returns false if missing/invalid -- callers should return immediately
 * when this returns false.
 */
export function requireValidPairingToken(
  req: FastifyRequest,
  reply: FastifyReply,
  deviceStore: DeviceStore,
  deviceId: string,
): boolean {
  const header = req.headers.authorization;
  const token = header?.startsWith("Bearer ") ? header.slice("Bearer ".length) : undefined;
  if (!token || !deviceStore.isValidToken(deviceId, token)) {
    reply.code(401).send({ schemaVersion: 1, error: "invalid_or_missing_pairing_token" });
    return false;
  }
  return true;
}
