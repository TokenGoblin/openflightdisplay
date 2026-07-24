# Provisioning and Pairing

See `docs/ARCHITECTURE.md` § "Discovery and pairing flow" for the high-level rationale. This document is the step-by-step user- and implementer-facing walkthrough.

## First-time setup (user's perspective)

1. Power on the Core2. If it has no saved Wi-Fi credentials, its screen shows: "Connect to Wi-Fi network `OpenFlightDisplay-Setup-XXXX` to continue," where `XXXX` is derived from its device ID.
2. On a phone or tablet, join that Wi-Fi network. Most devices will auto-open a captive-portal page; if not, browse to `http://192.168.4.1/`.
3. Enter the home Wi-Fi SSID and password on that page. Submit.
4. The Core2 attempts to join the home network. On success, it reboots into station mode and shows its assigned IP, a 6-digit pairing code, and a QR code encoding both.
5. Open the OpenFlightDisplay tablet PWA, choose "Add Display," and either scan the QR code or type in the IP + code manually.
6. The PWA claims the pairing code (single-use, 10-minute expiry) and receives a pairing token.
7. The PWA walks through location + monitoring radius, then writes that configuration (plus the gateway's address) to the Core2 using the pairing token.
8. The Core2 persists this to LittleFS and connects to the gateway. Both the Core2 and the PWA now show live aircraft.

## Failure handling (binding requirements, not aspirational)

- **Wrong Wi-Fi password**: the Core2 retries a bounded number of times, then automatically falls back to re-opening its SoftAP + captive portal rather than looping forever or bricking on a bad credential.
- **Expired/wrong pairing code**: the Core2's `/pair` endpoint returns a clear `invalid_or_expired_code` error (see `docs/PROTOCOL.md`); the PWA surfaces this as "That code has expired — check the Core2's screen for a new one" rather than a generic failure.
- **Interrupted provisioning** (power loss mid-setup): config writes are atomic (write-temp-then-rename on LittleFS), so an interrupted write can't leave a half-written, corrupt config. On next boot, an unwritten/absent config just means "still needs setup," not a crash.
- **Gateway unreachable at pairing time**: the PWA can still complete Core2 pairing and location/radius entry (those are stored regardless); the Core2 shows "Configuration required" → "Data source unavailable" rather than blocking the whole flow on the gateway being up.

## Re-pairing / multiple tablets

Any tablet that knows the Core2's IP and current pairing state can re-pair by requesting a new pairing code display on the Core2 itself (a physical/on-screen action on the Core2 — not something a remote, unpaired client can trigger, to prevent a stranger on the LAN from taking over an already-configured device). This "re-pair" on-device action is the Phase 1 answer to "recovery if you lose your original tablet"; a full revoke-token/multi-client management UI is Phase 5 (fleet management).

## Factory reset

Deferred to Phase 2 (tracked in `docs/FEATURE_PARITY_MATRIX.md`). Phase 1 has no on-device factory-reset gesture yet; re-flashing is the only reset path this session's firmware supports.
