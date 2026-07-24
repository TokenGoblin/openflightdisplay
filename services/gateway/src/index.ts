import { loadEnv } from "./config/env.js";
import { createLogger } from "./lib/logger.js";
import { DeviceStore } from "./lib/deviceStore.js";
import { buildApp } from "./server.js";

async function main(): Promise<void> {
  const env = loadEnv();
  const logger = createLogger(env);

  const deviceStore = new DeviceStore(env.DEVICE_STORE_PATH, logger);
  await deviceStore.load();

  const { app, poller } = await buildApp(env, logger, deviceStore);

  poller.start();

  await app.listen({ host: env.HOST, port: env.PORT });
  logger.info({ host: env.HOST, port: env.PORT, provider: env.AVIATION_PROVIDER }, "gateway listening");

  for (const signal of ["SIGINT", "SIGTERM"] as const) {
    process.on(signal, () => {
      logger.info({ signal }, "shutting down");
      poller.stop();
      void app.close().then(() => process.exit(0));
    });
  }
}

main().catch((err) => {
  // eslint-disable-next-line no-console
  console.error("Fatal error starting gateway:", err);
  process.exit(1);
});
