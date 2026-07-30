#pragma once

#include "app/app_context.h"
#include "domain/aircraft.h"

namespace ofd::app {

// Pointer to the global AppContext, set by main.cpp in setup().
// Used by the display module to read cached battery state without
// introducing a circular header dependency.
extern AppContext* s_ctx;

// Airport-FIDS-style renderer for the Core2's 320x240 panel. Every method
// is a distinct, explicit screen state -- see docs/DISPLAY_UI.md for
// the full state diagram and docs/PRODUCT_REQUIREMENTS.md /
// docs/ARCHITECTURE.md for why an indefinite spinner is never acceptable
// here.
//
// Every render* method draws its own header (title + live Wi-Fi/battery
// status, read directly from AppContext) so the masthead is always
// present and always current, then draws its own body content below it.
// The header is a small persistent M5Canvas sprite; the body is drawn
// directly to the panel with a single region-clear (never a full
// fillScreen) -- see the "Sprite and buffering strategy" section of
// docs/DISPLAY_UI.md for why that split was chosen over a full-screen
// sprite on this specific (PSRAM-less) hardware.
//
// Every method below except renderBoot/renderProvisioning/
// renderLocationRequired/renderOtaProgress also draws the bottom tab bar
// (FLIGHT / DETAIL / SYSTEM, mapped directly to BtnA/BtnB/BtnC -- see
// main.cpp's loop()) and reads which tab is active from
// s_ctx->currentPage, the same ambient-AppContext pattern already used
// for battery/Wi-Fi in the header. Callers don't pass the page in.
class Display {
 public:
  void begin();

  void renderBoot(const char* firmwareVersion);
  void renderProvisioning(const char* apName);

  // Shown once Wi-Fi is up but no monitoring area has been configured yet
  // (i.e. the device needs to be paired from the tablet/phone setup
  // wizard). `ipAddress` and `code` are shown large enough to be typed
  // manually -- see docs/PROVISIONING.md for why QR scanning isn't the
  // primary path on this hardware.
  void renderLocationRequired(const char* ipAddress, const char* pairingCode);

  // The main nearest-aircraft flight-information screen. `stale` flows
  // into the STATUS cell only -- every other field keeps showing the
  // aircraft's last known values rather than blanking, per
  // docs/DISPLAY_UI.md's staleness rule.
  void renderAircraft(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale);

  // Configured and connected, but no aircraft update has arrived yet.
  void renderSearching();

  // Configured, connected, and the provider is healthy, but zero
  // aircraft are currently within the configured radius. `hasClock`/
  // `timeHhMm` optionally show the current local time as a secondary,
  // non-essential detail (NTP-derived; omitted if time isn't known yet).
  void renderNoTraffic(bool hasClock, const char* timeHhMm);

  void renderWifiOffline();
  void renderApiError();

  // DETAIL tab: everything about the current nearest aircraft that
  // doesn't fit on the primary FLIGHT screen -- squawk, exact
  // coordinates, and bearing *from the observer* (which way to
  // physically look), as distinct from the aircraft's own track.
  void renderAircraftDetail(const ofd::AircraftState& aircraft, uint32_t ageSeconds, bool stale);

  // DETAIL tab, shown instead of renderAircraftDetail when there's
  // currently no aircraft to elaborate on (searching/no-traffic/stale
  // timeout) -- still needs the tab bar so the user isn't stuck.
  void renderDetailPlaceholder();

  // SYSTEM tab: Wi-Fi/data-source/battery/device diagnostics. Always
  // renderable regardless of aircraft state -- in fact most useful
  // exactly when something else is wrong.
  void renderSystemInfo();

  // percent 0-100, complete=true for the success screen.
  void renderOtaProgress(uint8_t percent, bool complete, const char* status);

  // Call once per loop() iteration.
  void update();
};

}  // namespace ofd::app
