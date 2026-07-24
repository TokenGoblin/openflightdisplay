import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// @testing-library/react's built-in auto-cleanup only registers itself
// when it finds a *global* `afterEach` -- which Vitest doesn't provide
// unless `test.globals: true` is set (it isn't, here). Without this,
// each render() across tests in the same file accumulates in the same
// jsdom document instead of being unmounted between tests, causing
// spurious "found multiple elements" failures.
afterEach(() => {
  cleanup();
});
