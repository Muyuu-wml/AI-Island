import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { metadata, Monitor } from "./monitor.js";

function send(bridge, events) {
  if (!events.length) return Promise.resolve();
  return new Promise((resolve) => {
    const child = spawn(bridge, ["OpenCode"], { windowsHide: true, stdio: ["pipe", "ignore", "ignore"] });
    const timeout = setTimeout(() => { child.kill(); resolve(); }, 2500);
    const done = () => { clearTimeout(timeout); resolve(); };
    child.on("error", done);
    child.on("close", done);
    child.stdin.on("error", () => {});
    child.stdin.end(JSON.stringify(events.map(event => ({ ...event, process_id: process.pid }))));
  });
}

// V2 accepts a default-exported plugin definition with id and setup.
export default {
  id: "ai-island",
  async setup(ctx) {
    if (process.platform !== "win32") return;
    let bridge;
    try {
      bridge = JSON.parse(await readFile(new URL("./bridge.json", import.meta.url), "utf8")).bridge;
      if (typeof bridge !== "string" || !path.isAbsolute(bridge)) return;
    } catch { return; }
    const monitor = new Monitor();
    const controller = new AbortController();
    const same = (a, b) => typeof a === "string" && typeof b === "string" && path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
    const queue = [];
    let draining = Promise.resolve(), busy = false;
    async function consume(event) {
      let directory = event.directory;
      if (!directory) {
        const session = await ctx.session.get({ sessionID: event.sessionID }, { signal: AbortSignal.timeout(2000) });
        directory = session.location.directory;
      }
      // The subscription spans the entire service; each location owns its sessions.
      if (!same(directory, ctx.location.directory)) return;
      if (event.type === "session.moved" && same(event.destination, directory)) return;
      await send(bridge, monitor.apply(event, directory));
    }
    function enqueue(event) {
      if (queue.length >= 1024) queue.shift();
      queue.push(event);
      if (busy) return;
      busy = true;
      draining = (async () => {
        try {
          while (queue.length) {
            try { await consume(queue.shift()); } catch { /* Monitoring must not affect OpenCode. */ }
          }
        } finally { busy = false; }
      })();
    }
    void (async () => {
      while (!controller.signal.aborted) {
        try {
          for await (const event of ctx.event.subscribe({ signal: controller.signal })) {
            if (controller.signal.aborted) break;
            const value = metadata(event);
            if (value) enqueue(value);
          }
        } catch { /* The public stream is live-only; reconnect after service errors. */ }
        try { await delay(1000, undefined, { signal: controller.signal }); } catch { break; }
      }
    })();
    return async () => {
      controller.abort();
      queue.length = 0;
      await draining;
      try { await send(bridge, monitor.close()); } catch { }
    };
  },
};
