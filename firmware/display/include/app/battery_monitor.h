#pragma once

#include "app/app_context.h"

namespace ofd::app {

// Polls M5.Power every ~10 seconds and caches the result in
// ctx.battery. Call `pollBattery()` from the main loop at least once
// per iteration — it handles its own throttling internally.
void pollBattery(AppContext& ctx);

}  // namespace ofd::app