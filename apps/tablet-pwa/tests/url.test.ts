import { describe, expect, it } from "vitest";
import { normalizeHttpUrl, toWebSocketBaseUrl } from "../src/lib/url";

describe("normalizeHttpUrl", () => {
  it("leaves a well-formed http(s) URL untouched", () => {
    expect(normalizeHttpUrl("http://192.168.1.50:8787")).toBe("http://192.168.1.50:8787");
    expect(normalizeHttpUrl("https://example.com")).toBe("https://example.com");
  });

  it("prepends http:// when no scheme was typed", () => {
    // Verified needed on real hardware: a bare "ip:port" (no scheme) fed
    // into the old naive .replace(/^http/, "ws") produced a URL that
    // didn't start with ws://, which the firmware rejected with a 400.
    expect(normalizeHttpUrl("192.168.1.50:8787")).toBe("http://192.168.1.50:8787");
  });

  it("trims whitespace", () => {
    expect(normalizeHttpUrl("  192.168.1.50:8787  ")).toBe("http://192.168.1.50:8787");
  });
});

describe("toWebSocketBaseUrl", () => {
  it("converts http:// to ws://", () => {
    expect(toWebSocketBaseUrl("http://192.168.1.50:8787")).toBe("ws://192.168.1.50:8787");
  });

  it("converts https:// to wss://", () => {
    expect(toWebSocketBaseUrl("https://example.com")).toBe("wss://example.com");
  });

  it("strips a trailing slash", () => {
    expect(toWebSocketBaseUrl("http://192.168.1.50:8787/")).toBe("ws://192.168.1.50:8787");
  });
});
