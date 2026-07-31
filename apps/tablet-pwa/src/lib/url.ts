/**
 * Verified needed on real hardware: a naive `.replace(/^http/, "ws")`
 * silently produces a malformed URL (not starting with ws://) whenever
 * the input wasn't typed with an explicit http(s):// scheme -- the
 * firmware correctly rejected the result with a 400. Parsing with the
 * real URL API instead of string-replacing avoids that whole class of
 * bug (missing scheme, trailing slash, https, unusual casing, ...).
 */

/** Ensures `raw` has an http(s):// scheme, defaulting to http:// if missing. */
export function normalizeHttpUrl(raw: string): string {
  const trimmed = raw.trim();
  if (/^https?:\/\//i.test(trimmed)) return trimmed;
  return `http://${trimmed}`;
}

/** http://host[:port] -> ws://host[:port], https:// -> wss://. */
export function toWebSocketBaseUrl(httpUrl: string): string {
  const url = new URL(httpUrl);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString().replace(/\/$/, "");
}

/**
 * Checks a (not-yet-normalized) address is parseable once normalized --
 * lets form fields validate immediately at entry with a clear message,
 * instead of a raw URL-parser TypeError surfacing several wizard steps
 * later (verified needed on real hardware).
 */
export function isValidAddress(raw: string): boolean {
  try {
    // Constructed only for its throw-on-invalid behaviour; the instance
    // is intentionally discarded.
    new URL(normalizeHttpUrl(raw));
    return true;
  } catch {
    return false;
  }
}
