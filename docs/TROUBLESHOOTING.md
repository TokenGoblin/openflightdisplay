# Troubleshooting

Quick diagnostic steps for common issues. For architecture and protocol details see `docs/ARCHITECTURE.md` and `docs/PROTOCOL.md`.

## General diagnostics

1. **Check the gateway status endpoint:** `http://<gateway-ip>:8787/api/v1/status` — returns the provider health, last successful poll time, and connected device count.
2. **Check the Core2 status endpoint:** `http://<core2-ip>/api/v1/status` — returns the device's own Wi-Fi, gateway connection, and config state.
3. **Check each display's explicit status screen:** every failure mode shows a self-explanatory banner rather than a blank or spinning screen. If you see a blank screen, that's a bug — please file an issue.

## Flashing the Core2

**The auto-reset after `pio run -t upload` does not reliably bring the board out of download mode on some unit/cable combinations.** If the flash appears to hang at "waiting for download," power-cycle the Core2 manually (unplug USB, replug). This has been observed on a M5Stack Core2 with a CH9102 USB-UART chip.

## Pairing / setup

### "I scanned the QR code but nothing happened"

This is expected. **QR scanning does not work** for pairing in this system's normal deployment because:
- A phone's default camera app opens the encoded URL as a plain link (the Core2 serves an explanatory page there, but it doesn't complete pairing).
- The PWA's own in-app camera scanner requires `navigator.mediaDevices`, which browsers only expose in a secure (HTTPS) context — and this system runs over plain HTTP on the LAN by design.

**Use manual IP + code entry instead.** It's the PWA's default tab under "Add your display" — enter the Core2's IP address (shown on its screen) and the 6-digit pairing code.

### "I lost my setup progress on the tablet"

The PWA persists wizard progress to `localStorage` after every step. If you accidentally close the tab or it reloads (common on mobile), just re-open the PWA — it will resume from the last completed step. If it doesn't, your browser's private/incognito mode may be blocking localStorage.

### "The PWA says 'Could not pair with the display'"

- Verify the Core2's IP address is correct and reachable from the tablet: open `http://<core2-ip>/api/v1/status` in the tablet's browser. If it doesn't load, the tablet is on a different network or the Core2's Wi-Fi has dropped.
- The pairing code expires after 5 minutes. If the Core2 has been sitting on the pairing screen for a while, it auto-regenerates — use the code currently shown on the screen, not one from memory.
- CORS errors: if you see a generic "network error" in the browser console and you're running the PWA from its dev server (`npm run dev`), make sure the gateway has CORS enabled (enabled by default in `server.ts`).

## Gateway

### "The gateway says it's using the mock provider but I set adsb.lol in .env"

The `.env` file must be loaded **before** `loadEnv()` reads from `process.env`. Ensure `import "dotenv/config"` is the very first import in `index.ts` (it is by default in this repo). If you've restructured imports, double-check this.

Verify with: `curl http://localhost:8787/api/v1/status` — the `provider.id` field tells you which provider is actually active.

### "The gateway process crashed"

Check for:
1. **Port already in use:** if another process is on port 8787, the gateway fails to start. Change `PORT` in `.env`.
2. **File-write race:** a burst of rapid WebSocket reconnects previously caused an unhandled promise rejection in `DeviceStore` (fixed in git history, with a regression test). If you see this again, ensure you're on the latest `main` and that `tests/deviceStore.test.ts` passes.

### "The gateway shows connected devices but no aircraft data"

- Verify your provider is actually returning data: the mock provider always returns synthetic aircraft; adsb.lol returns real data but can occasionally be empty for remote locations or during provider maintenance.
- Check the gateway logs for provider fetch errors (`err` field in the pino log output).
- After 3 consecutive provider failures, the gateway marks the provider as "unavailable" and stops emitting aircraft updates (it emits a provider-status message instead). The displays will show "Data source unavailable."

## Core2 display

### "Stuck on Wi-Fi setup / never connects"

- The Core2 only supports WPA2 networks (WPA3 support is an ESP32 SDK capability but unverified on this hardware).
- Hidden SSIDs are not supported.
- If the SoftAP (`OFD-Setup-XXXXXX`) doesn't appear, press-and-hold the Core2's power button for ~6 seconds to force a hard reset (this clears the saved Wi-Fi and restarts provisioning).

### "Blank screen / frozen display"

The Core2 has no indefinite spinner or "preparing" state. Every possible state renders an explicit message. If the screen is truly blank:
1. The display backlight may be at 0 — check `displayProfile.brightness` in the config (default is 200, range 0-255).
2. A task watchdog reset may have occurred — connect a serial monitor (115200 baud) and look for "Task watchdog" messages.

### "The display shows stale data"

The Core2 uses a color-coded age dot in the header:
- **Green** (<5s): fresh data
- **Amber** (5-30s): data may be slightly stale
- **Red** (>30s): data is stale — check the gateway and provider are still running

### "Screen shows 'No matching aircraft' instead of the clock"

The clock appears when NTP time has synced (can take 15-30 seconds after Wi-Fi connects). Until then, "No matching aircraft" is shown. If the clock never appears after several minutes, the NTP server may be unreachable from your network.

## Known hardware quirks

- **Auto-reset after flash unreliable:** manual power-cycle needed on some units (see "Flashing" above).
- **Wi-Fi outage behavior unverified:** pulling the router's power was not tested (would disrupt the tester's home network). Wi-Fi disconnect/reconnect logic is implemented but this exact scenario hasn't been confirmed on real hardware.
- **Multi-day soak test not done:** no continuous-operation/heap-growth test has been performed. Worth running if deploying 24/7.