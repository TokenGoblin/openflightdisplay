/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      manifest: {
        name: "OpenFlightDisplay",
        short_name: "OFD",
        description: "See what's flying overhead",
        start_url: "/",
        display: "standalone",
        background_color: "#0b1220",
        theme_color: "#0b1220",
        icons: [{ src: "icons/icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any maskable" }],
      },
      workbox: {
        // App-shell precache only; live aircraft data is intentionally
        // never cached -- a stale cached aircraft position rendered as
        // "live" would violate docs/PRODUCT_REQUIREMENTS.md's data-age
        // honesty requirement. The in-app status banner (not the service
        // worker) is what tells the user data is stale/unavailable.
        navigateFallback: "/index.html",
        runtimeCaching: [],
      },
    }),
  ],
  test: {
    environment: "jsdom",
    setupFiles: ["./tests/setup.ts"],
    include: ["tests/**/*.test.{ts,tsx}"],
  },
});
