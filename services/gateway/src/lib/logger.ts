import pino from "pino";
import type { GatewayEnv } from "../config/env.js";

/**
 * Structured logger with redaction for anything secret-shaped. Per
 * docs/SECURITY_AND_PRIVACY.md: pairing tokens, API keys, and Wi-Fi
 * passwords must never appear in logs, even if a caller accidentally
 * passes one through.
 */
export function createLogger(env: Pick<GatewayEnv, "LOG_LEVEL">) {
  return pino({
    level: env.LOG_LEVEL,
    redact: {
      paths: [
        "*.pairingToken",
        "*.apiKey",
        "*.wifiPassword",
        "*.password",
        "req.headers.authorization",
        "*.headers.authorization",
      ],
      censor: "[redacted]",
    },
  });
}

export type Logger = ReturnType<typeof createLogger>;
