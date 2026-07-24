import type { GatewayEnv } from "../config/env.js";
import { AdsbLolProvider } from "./adsblol.js";
import { MockProvider } from "./mock.js";
import { ReplayProvider } from "./replay.js";
import type { AviationDataProvider } from "./provider.js";

export * from "./provider.js";
export { MockProvider } from "./mock.js";
export { ReplayProvider } from "./replay.js";
export { AdsbLolProvider } from "./adsblol.js";

export function createProvider(env: GatewayEnv): AviationDataProvider {
  switch (env.AVIATION_PROVIDER) {
    case "mock":
      return new MockProvider();
    case "replay":
      return new ReplayProvider(env.REPLAY_FIXTURE_PATH);
    case "adsblol":
      return new AdsbLolProvider();
    default: {
      const exhaustive: never = env.AVIATION_PROVIDER;
      throw new Error(`Unknown AVIATION_PROVIDER: ${exhaustive}`);
    }
  }
}
