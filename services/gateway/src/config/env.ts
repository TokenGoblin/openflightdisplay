import { z } from "zod";

const EnvSchema = z.object({
  AVIATION_PROVIDER: z.enum(["mock", "replay", "adsblol"]).default("mock"),
  // PORT=0 is valid and means "ask the OS for any free ephemeral port" --
  // used by tests that spin up a real server on an unpredictable port.
  PORT: z.coerce.number().int().min(0).max(65535).default(8787),
  HOST: z.string().min(1).default("0.0.0.0"),
  REPLAY_FIXTURE_PATH: z.string().default("tests/fixtures/one-commercial-aircraft.json"),
  ADSBLOL_API_KEY: z.string().optional(),
  DEVICE_STORE_PATH: z.string().default("data/devices.json"),
  LOG_LEVEL: z.enum(["fatal", "error", "warn", "info", "debug", "trace"]).default("info"),
});

export type GatewayEnv = z.infer<typeof EnvSchema>;

/**
 * Parsed once at startup. Throws with a clear message on an invalid
 * environment rather than starting up in a half-configured state.
 */
export function loadEnv(source: NodeJS.ProcessEnv = process.env): GatewayEnv {
  const result = EnvSchema.safeParse(source);
  if (!result.success) {
    throw new Error(`Invalid gateway environment configuration: ${result.error.message}`);
  }
  return result.data;
}
