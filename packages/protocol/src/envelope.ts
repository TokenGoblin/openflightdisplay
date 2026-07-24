import { z } from "zod";

/**
 * Every message on the wire (REST or WebSocket) carries this. A receiver
 * that doesn't understand a given schemaVersion MUST reject the message
 * with a clear "unsupported protocol version" status rather than
 * best-effort parsing it. See docs/PROTOCOL.md.
 */
export const CURRENT_SCHEMA_VERSION = 1 as const;

export const SchemaVersionedSchema = z.object({
  schemaVersion: z.literal(CURRENT_SCHEMA_VERSION),
});
