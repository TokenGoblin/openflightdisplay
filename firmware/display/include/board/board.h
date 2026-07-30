#pragma once

#include <cstdint>

#include "app/page.h"

// Board abstraction layer.
//
// Everything in the firmware above this header is board-agnostic: the
// domain/ layer was always hardware-independent, and app/ is written
// against the traits and hooks declared here plus the layout profile in
// app/ui_layout.h. Exactly one src/board/*.cpp is compiled into any
// given build, selected by build_src_filter in platformio.ini.
//
// Adding a third board should mean: a new src/board/<name>.cpp, a new
// include/app/ui_layout_<name>.h defining the same constant names as the
// existing profiles, a new OFD_BOARD_<NAME> block below, and a new
// [env:<name>] -- and nothing else.

#if defined(OFD_BOARD_CORE2) + defined(OFD_BOARD_TAB5) != 1
#error "Exactly one of OFD_BOARD_CORE2 / OFD_BOARD_TAB5 must be defined (see platformio.ini build_flags)"
#endif

namespace ofd::board {

// ---- compile-time board traits ----
//
// kDeviceIdPrefix is user-visible in three places that must agree: the
// device id ("<prefix>-8a2f19", shown on the SYSTEM page and used as the
// mDNS/OTA hostname), the mDNS TXT "type" record the tablet PWA reads to
// tell paired devices apart, and the setup access-point name. Keep it
// short, lowercase, and free of characters that are awkward in a DNS
// label or an SSID.
//
// kScreenW/kScreenH are the *expected* panel dimensions in the firmware's
// working orientation, after beginDisplay() has applied its rotation.
// They must match the corresponding ui_layout_<board>.h profile;
// app::Display::begin() checks them against the panel's real reported
// size at boot and logs loudly on a mismatch rather than silently
// drawing off-screen.

#if defined(OFD_BOARD_CORE2)

constexpr const char* kDeviceIdPrefix = "core2";
constexpr const char* kProductName = "M5Stack Core2";
constexpr int kScreenW = 320;
constexpr int kScreenH = 240;
// Three capacitive pads below the panel, exposed by M5Unified as
// BtnA/BtnB/BtnC.
constexpr bool kHasPhysicalButtons = true;
// This board has no usable PSRAM (docs/CORE2_HARDWARE.md confirmed its
// absence on the unit this was tested against), so the renderer keeps a
// small header sprite in internal RAM and draws the body straight to the
// panel. See docs/DISPLAY_UI.md "Sprite and buffering strategy".
constexpr bool kUseFullScreenCanvas = false;

#elif defined(OFD_BOARD_TAB5)

constexpr const char* kDeviceIdPrefix = "tab5";
constexpr const char* kProductName = "M5Stack Tab5";
constexpr int kScreenW = 1280;
constexpr int kScreenH = 720;
// No buttons other than power/reset -- navigation is by tapping the
// on-screen tab bar.
constexpr bool kHasPhysicalButtons = false;
// 32MB of PSRAM, and a 1280x720 panel where a region-clear-and-redraw
// would be very visible. A full-screen 16-bit canvas is ~1.84MB, which
// is a rounding error against this board's PSRAM budget.
constexpr bool kUseFullScreenCanvas = true;

#endif

// Board-specific hardware bring-up. Called from setup() immediately
// after M5.begin() and, critically, *before any Wi-Fi call whatsoever*.
//
// On the Tab5 this is where the SDIO link to the ESP32-C6 radio
// co-processor is configured; skip it and every Wi-Fi call fails,
// because the P4 has no radio of its own. On the Core2 it does nothing.
void begin();

// Panel rotation, brightness and any other display-controller setup that
// differs per board. Called by app::Display::begin() before anything is
// drawn.
void beginDisplay();

// Page-navigation input, polled once per loop() iteration. Returns the
// page the user has just asked for, or `current` when they haven't asked
// for anything -- so the caller can compare against the active page and
// only repaint on an actual change.
//
// Requires that M5.update() has already run this iteration (Display's
// update() does it), since both implementations read state that
// M5.update() refreshes.
app::DetailPage pollPageRequest(app::DetailPage current);

}  // namespace ofd::board
