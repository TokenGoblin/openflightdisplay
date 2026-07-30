#pragma once

#include <cstdint>

namespace ofd::app::layout {

// Every pixel region for the M5Stack Core2's 320x240 landscape
// airport-FIDS screen. The renderer must not contain a layout magic
// number that isn't defined here -- see docs/DISPLAY_UI.md for the
// annotated diagram this mirrors.
//
// Every value below is the one the Core2 design was drawn and
// hardware-tested at. The file gained constants when the Tab5 profile
// was added (things like kHeaderIconY that used to be inline literals in
// display.cpp), but no existing number changed: the two profiles have to
// define the same names for the shared renderer to compile against
// either, and a name that only existed as a literal couldn't be
// overridden per board.

constexpr int kScreenW = 320;
constexpr int kScreenH = 240;

// ---- header (status masthead) ----
constexpr int kHeaderH = 30;
constexpr int kHeaderTitleX = 8;
constexpr int kHeaderTitleY = 7;
constexpr int kHeaderAccentLineY = kHeaderH;  // 1px rule, full width

// Right-hand status cluster, laid out right-to-left from the screen edge.
constexpr int kHeaderRightEdge = 312;
constexpr int kHeaderIconY = 9;                  // shared top edge for both status icons
constexpr int kHeaderBatteryPercentZoneW = 36;   // reserves room for "100%" unconditionally
constexpr int kHeaderBatteryPercentY = 12;
constexpr int kHeaderBatteryIconGap = 4;
constexpr int kHeaderBatteryIconW = 20;
constexpr int kHeaderBatteryIconH = 11;
constexpr int kHeaderBatteryNubW = 2;
constexpr int kHeaderBatteryNubInsetY = 3;   // nub is inset this far from top and bottom
constexpr int kHeaderBatteryFillInset = 2;   // charge bar inset inside the outline
constexpr int kHeaderBatteryBoltThickness = 1;
constexpr int kHeaderBatteryVisualW = kHeaderBatteryIconW + kHeaderBatteryNubW;
constexpr int kHeaderWifiGap = 8;
constexpr int kHeaderWifiIconW = 16;
constexpr int kHeaderWifiIconH = 12;
// Three ascending signal bars, bottom-aligned within the icon box.
constexpr int kHeaderWifiBarW = 3;
constexpr int kHeaderWifiBarStep = 5;
constexpr int kHeaderWifiBarH1 = 4;
constexpr int kHeaderWifiBarH2 = 7;
constexpr int kHeaderWifiBarH3 = 11;

constexpr int kHeaderBatteryPercentRightX = kHeaderRightEdge;
constexpr int kHeaderBatteryIconRightX = kHeaderBatteryPercentRightX - kHeaderBatteryPercentZoneW - kHeaderBatteryIconGap;
constexpr int kHeaderBatteryIconLeftX = kHeaderBatteryIconRightX - kHeaderBatteryVisualW;
constexpr int kHeaderWifiIconRightX = kHeaderBatteryIconLeftX - kHeaderWifiGap;
constexpr int kHeaderWifiIconLeftX = kHeaderWifiIconRightX - kHeaderWifiIconW;

// Left title zone ends with margin before the right cluster begins.
constexpr int kHeaderTitleMaxRightX = kHeaderWifiIconLeftX - 10;

// ---- identity block (callsign / airline / type / ICAO) ----
// On this board the identity block spans the full panel width; on a
// board with a side column it spans only the primary column, which is
// what kIdentityBlockW exists to express.
constexpr int kIdentityBlockW = kScreenW;
constexpr int kIdentityLeftX = 10;
constexpr int kIdentityRightMarginX = 8;

constexpr int kCallsignY = 32;
constexpr int kCallsignRowH = 46;  // 32..78

constexpr int kAirlineTypeY = kCallsignY + kCallsignRowH;      // 78
constexpr int kAirlineTypeH = 22;                              // 78..100
constexpr int kAirlineTextOffsetY = 4;                         // airline baseline within that row
constexpr int kTypeBadgeW = 76;
constexpr int kTypeBadgeX = kIdentityBlockW - kIdentityRightMarginX - kTypeBadgeW;  // 236
constexpr int kAirlineMaxWidth = kTypeBadgeX - kIdentityLeftX - 8;                  // gap before badge

constexpr int kIcaoY = kAirlineTypeY + kAirlineTypeH;  // 100
constexpr int kIcaoH = 14;                             // 100..114
constexpr int kIcaoTextOffsetY = 2;

constexpr int kIdentityDividerY = kIcaoY + kIcaoH;  // 114

// ---- bottom tab bar (FLIGHT / DETAIL / SYSTEM) ----
// Present on every operational-state screen (i.e. everything shown once
// past Boot/Provisioning/LocationRequired) so page navigation always
// works the same way regardless of what's currently on screen -- see
// docs/DISPLAY_UI.md's "Page navigation" section. On this board the
// three columns sit directly above the three physical buttons.
constexpr int kTabBarH = 18;
constexpr int kTabBarY = kScreenH - kTabBarH;  // 222..240
constexpr int kTabBarColBoundaries[4] = {0, 107, 213, kScreenW};
constexpr int kTabBarActiveIndicatorH = 2;

// ---- operational grid (2 rows x 3 columns) ----
constexpr int kGridTop = kIdentityDividerY + 4;  // 118
constexpr int kGridBottom = kTabBarY;            // 222
constexpr int kGridRowH = (kGridBottom - kGridTop) / 2;  // 52

constexpr int kGridColBoundaries[4] = {0, 107, 213, kScreenW};

constexpr int kGridCellPadX = 8;
constexpr int kGridLabelOffsetY = 6;    // from cell top
constexpr int kGridValueOffsetY = 19;   // from cell top, top edge of the bold value font
constexpr int kGridCaptionOffsetY = 42; // from cell top, small unit/compass/subcaption line
constexpr int kGridValueUnitGap = 4;         // horizontal gap between value and inline unit
constexpr int kGridUnitBaselineOffsetY = 9;  // inline unit's drop below the value's top edge

// STATUS cell's colored left accent bar.
constexpr int kStatusAccentBarW = 3;
constexpr int kStatusAccentBarBottomInset = 8;
constexpr int kStatusAccentTextGap = 4;

// Hollow-circle degree mark drawn after a bearing/track value.
constexpr int kDegreeMarkGap = 2;      // from the last digit
constexpr int kDegreeMarkOffsetX = 3;  // circle center, from the mark's left edge
constexpr int kDegreeMarkOffsetY = 3;  // circle center, from the value's top edge
constexpr int kDegreeMarkRadius = 2;

// ---- generic full-screen status layout (boot/searching/error/etc.) ----
constexpr int kStatusTitleY = 74;
constexpr int kStatusBodyY = 108;
constexpr int kStatusFootnoteY = 196;  // clear of the tab bar at kTabBarY (222)
constexpr int kStatusMargin = 24;
constexpr int kNoTrafficClockY = kStatusFootnoteY - 10;

// ---- detail / system pages: a simple label/value list ----
// Label and value both use the micro bitmap font (~8px tall at this
// size), so 20px/row leaves a comfortable gap -- up to 8 rows fits well
// within the header-to-tab-bar space (40 + 8*20 = 200, vs. kTabBarY =
// 222).
constexpr int kDetailTop = 40;
constexpr int kDetailRowH = 20;
constexpr int kDetailLabelX = 12;
constexpr int kDetailValueX = 130;

// ---- Wi-Fi provisioning screen ----
constexpr int kProvisionPromptY = kStatusTitleY - 20;
constexpr int kProvisionBoxY = kStatusTitleY + 6;
constexpr int kProvisionBoxH = 44;
constexpr int kProvisionBoxRadius = 4;
constexpr int kProvisionBoxTextInset = 16;
constexpr int kProvisionApNameY = kStatusTitleY + 18;

// ---- setup-required (pairing) screen ----
constexpr int kQrModuleSize = 4;
constexpr int kQrX = 14;
constexpr int kQrQuietZone = 4;
constexpr int kSetupTextGapX = 24;  // between the QR block and the text column
constexpr int kSetupLabelY = 46;
constexpr int kSetupIpY = 60;
constexpr int kSetupPathY = 92;
constexpr int kSetupHintY = 130;

// ---- OTA progress screen ----
constexpr int kOtaTitleY = kStatusTitleY - 20;
constexpr int kOtaStatusY = kStatusTitleY + 14;
constexpr int kOtaBarY = kStatusTitleY + 44;
constexpr int kOtaBarH = 22;
constexpr int kOtaBarRadius = 4;
constexpr int kOtaBarInset = 2;
constexpr int kOtaPercentGapY = 12;  // below the bar

}  // namespace ofd::app::layout
