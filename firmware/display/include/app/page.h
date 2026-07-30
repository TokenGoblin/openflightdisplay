#pragma once

#include <cstdint>

namespace ofd::app {

// Which of the three pages is currently selected.
//
// Lives in its own header (rather than in app_context.h, where it started)
// so board/board.h can name it without dragging in ConfigStore, the
// protocol structs and the rest of AppContext -- the board layer is
// deliberately the lowest-level module in the firmware and must not
// depend on application state.
//
// How a page is *selected* is board-specific: the Core2 maps its three
// physical buttons directly to these three values (each button always
// means the same page, never prev/next), while the Tab5 has no buttons
// and hit-tests taps against the on-screen tab bar. See
// ofd::board::pollPageRequest() and docs/DISPLAY_UI.md's "Page
// navigation" section.
enum class DetailPage : uint8_t { Flight, Detail, System };

}  // namespace ofd::app
