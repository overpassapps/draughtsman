/**
 * Entry point of the bundled sidecar (dist/sidecar.js). Reads newline-delimited JSON-RPC 2.0 requests from
 * stdin, writes one response per line to stdout, logs to stderr, opens no ports, and exits when stdin closes.
 * Draughtsman.Server spawns exactly one of these; see docs/adr/0002-crdt-and-layout-engine.md.
 */
import { createInterface } from 'node:readline';
import { methods } from './methods.ts';
import { handleLine, type JsonRpcResponse } from './rpc.ts';

const log = {
  warn(message: string): void {
    process.stderr.write(`sidecar: ${message}\n`);
  },
};

// stdout carries protocol frames only. Anything a dependency prints goes to stderr so it cannot corrupt a frame.
console.log = (...args: unknown[]): void => {
  process.stderr.write(`${args.map(String).join(' ')}\n`);
};
console.info = console.log;
console.debug = console.log;

let inFlight = 0;
let stdinClosed = false;

function write(response: JsonRpcResponse): void {
  process.stdout.write(`${JSON.stringify(response)}\n`);
}

/**
 * Never process.exit() here: a write to a pipe is asynchronous, so exiting on the same tick as the last
 * response drops the bytes still queued on stdout and the parent reads a truncated frame. With stdin closed
 * nothing holds the event loop open, so setting the code and letting it drain ends the process just as fast.
 */
function exitWhenDrained(): void {
  if (stdinClosed && inFlight === 0) {
    process.exitCode = 0;
  }
}

const lines = createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });

lines.on('line', (line: string) => {
  inFlight += 1;
  void handleLine(line, methods, log)
    .then((response) => {
      if (response !== null) {
        write(response);
      }
    })
    .catch((error: unknown) => {
      log.warn(`unhandled failure: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
    })
    .finally(() => {
      inFlight -= 1;
      exitWhenDrained();
    });
});

lines.on('close', () => {
  stdinClosed = true;
  exitWhenDrained();
});

// The parent went away: there is nobody to answer, so leave quietly rather than crash with EPIPE.
process.stdout.on('error', () => {
  process.exit(0);
});

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    // Stop reading and let whatever is in flight answer before the loop empties, for the same reason.
    stdinClosed = true;
    lines.close();
    process.stdin.unref();
    exitWhenDrained();
  });
}
