import { cli } from "../cli.js";
import { parseNumber } from "../number-utils.js";
import { startWebUiServer } from "../webui/server.js";

export const webui = cli()
  .name("webui")
  .description("Launch the Humanify browser UI")
  .option("-p, --port <port>", "Port to bind", "4173")
  .action(async (opts) => {
    const parsed = parseNumber(opts.port);
    const port = Number.isFinite(parsed) && parsed > 0 ? parsed : 4173;

    const server = await startWebUiServer({ port });
    console.log(`Humanify web UI ready at http://localhost:${server.port}`);
    console.log("Press Ctrl+C to stop the server.");

    let shuttingDown = false;
    const shutdown = async () => {
      if (shuttingDown) return;
      shuttingDown = true;
      console.log("\nShutting down web UI...");
      try {
        await server.close();
      } catch (err) {
        console.error("Failed to close web UI server", err);
      } finally {
        process.exit(0);
      }
    };

    process.once("SIGINT", shutdown);
    process.once("SIGTERM", shutdown);

    await new Promise(() => {
      /* keep process alive while server runs */
    });
  });
