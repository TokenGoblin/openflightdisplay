#pragma once

#include "board/board.h"

// Selects the layout profile for the board being built. Include this,
// never a ui_layout_<board>.h directly -- code that reaches for a
// specific profile is code that has quietly stopped being portable.
//
// Both profiles populate ofd::app::layout with the same constant names,
// so the shared renderer in src/app/display.cpp compiles unchanged
// against either. A name that exists in one profile and not the other is
// a compile error the first time the shared renderer touches it, which
// is exactly when you want to find out.

#if defined(OFD_BOARD_CORE2)
#include "app/ui_layout_core2.h"
#elif defined(OFD_BOARD_TAB5)
#include "app/ui_layout_tab5.h"
#endif

namespace ofd::app::layout {

// The profile and the board traits describe the same panel, so they had
// better agree. board::begin() has no way to check this and the symptom
// on a real device is subtle (everything drawn to a plausible-looking
// but wrong place), so catch it at compile time instead.
static_assert(kScreenW == ofd::board::kScreenW, "layout profile width disagrees with board traits");
static_assert(kScreenH == ofd::board::kScreenH, "layout profile height disagrees with board traits");

}  // namespace ofd::app::layout
