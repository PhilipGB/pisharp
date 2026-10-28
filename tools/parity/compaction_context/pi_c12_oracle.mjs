#!/usr/bin/env node
import { mkdir, writeFile } from "node:fs/promises";
import { mkdtemp, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { pathToFileURL } from "node:url";
import { execFileSync } from "node:child_process";

const PIN = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6";
const args = process.argv.slice(2);
const value = (name) => {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`missing ${name}`);
  return args[index + 1];
};
const piRoot = resolve(value("--pi"));
const output = resolve(value("--output"));
const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: piRoot, encoding: "utf8" }).trim();
if (head !== PIN) throw new Error(`Pi checkout is ${head}; expected ${PIN}`);
const fromPi = (relative) => import(pathToFileURL(resolve(piRoot, relative)).href);
const [{ createHarness }, { userMsg, assistantMsg }, { SessionManager, loadEntriesFromFile }] = await Promise.all([
  fromPi("packages/coding-agent/test/suite/harness.ts"),
  fromPi("packages/coding-agent/test/utilities.ts"),
  fromPi("packages/coding-agent/src/core/session-manager.ts"),
]);

async function compactWithCustomBoundary(boundarySelector, includeIncompleteToolCall = false) {
  const harness = await createHarness({
    settings: { compaction: { keepRecentTokens: 1 } },
    extensionFactories: [
      (pi) =>
        pi.on("session_before_compact", (event) => ({
          compaction: {
            summary: "extension checkpoint",
            firstKeptEntryId: boundarySelector(event),
            tokensBefore: event.preparation.tokensBefore,
          },
        })),
    ],
  });
  try {
    const manager = harness.sessionManager;
    if (includeIncompleteToolCall) {
      manager.appendMessage(userMsg("tool question"));
      manager.appendMessage({
        ...assistantMsg(""),
        content: [{ type: "toolCall", id: "call-c12", name: "read", arguments: { path: "notes.md" } }],
      });
      const retainedBoundary = manager.appendMessage(userMsg("retained question"));
      manager.appendMessage(assistantMsg("retained answer"));
      harness.session.agent.state.messages = manager.buildSessionContext().messages;
      try {
        await harness.session.compact();
        const entry = manager.getEntries().findLast((item) => item.type === "compaction");
        return { accepted: true, persisted: entry?.firstKeptEntryId === retainedBoundary };
      } catch (error) {
        return { accepted: false, persisted: false, error: error.message };
      }
    }

    manager.appendMessage(userMsg("first question"));
    manager.appendMessage(assistantMsg("first answer"));
    manager.appendMessage(userMsg("retained question"));
    manager.appendMessage(assistantMsg("retained answer"));
    harness.session.agent.state.messages = manager.buildSessionContext().messages;
    try {
      await harness.session.compact();
      const entry = manager.getEntries().findLast((item) => item.type === "compaction");
      return { accepted: true, persisted: entry?.firstKeptEntryId === "missing-boundary" };
    } catch (error) {
      return { accepted: false, persisted: false, error: error.message };
    }
  } finally {
    harness.cleanup();
  }
}

async function observeAfterHookCommit() {
  let observed = false;
  let entryId = null;
  const sessionRoot = await mkdtemp(join(tmpdir(), "pi-c12-durable-hook-"));
  const sessionManager = SessionManager.create(sessionRoot, join(sessionRoot, "sessions"));
  const harness = await createHarness({
    sessionManager,
    settings: { compaction: { keepRecentTokens: 1 } },
    extensionFactories: [
      (pi) => {
        pi.on("session_before_compact", (event) => ({
          compaction: {
            summary: "committed extension summary",
            firstKeptEntryId: event.preparation.firstKeptEntryId,
            tokensBefore: event.preparation.tokensBefore,
          },
        }));
        pi.on("session_compact", (event) => {
          entryId = event.compactionEntry.id;
          const sessionFile = harness.sessionManager.getSessionFile();
          observed = sessionFile !== undefined && loadEntriesFromFile(sessionFile)
            .some((entry) => entry.type === "compaction" && entry.id === entryId);
        });
      },
    ],
  });
  try {
    harness.sessionManager.appendMessage(userMsg("first question"));
    harness.sessionManager.appendMessage(assistantMsg("first answer"));
    harness.sessionManager.appendMessage(userMsg("retained question"));
    harness.sessionManager.appendMessage(assistantMsg("retained answer"));
    harness.session.agent.state.messages = harness.sessionManager.buildSessionContext().messages;
    await harness.session.compact();
    return { afterHookSawEntry: observed, entryPresentInSessionFile: entryId !== null && observed };
  } finally {
    harness.cleanup();
    await rm(sessionRoot, { recursive: true, force: true });
  }
}

const staleBoundary = await compactWithCustomBoundary(() => "missing-boundary");
const unbalancedToolBoundary = await compactWithCustomBoundary(
  (event) => event.branchEntries.find((entry) => entry.type === "message" &&
    entry.message?.role === "user" && entry.message.content === "retained question")?.id,
  true,
);
const result = {
  cases: {
    "C12.invalid-boundary-and-unbalanced-tools-are-rejected": {
      staleBoundaryRejected: !staleBoundary.accepted,
      staleBoundaryPersisted: staleBoundary.persisted,
      unbalancedToolBoundaryRejected: !unbalancedToolBoundary.accepted,
      unbalancedToolBoundaryPersisted: unbalancedToolBoundary.persisted,
    },
    "C12.after-hook-fires-only-after-durable-commit": await observeAfterHookCommit(),
  },
};
await mkdir(resolve(output, ".."), { recursive: true });
await writeFile(output, `${JSON.stringify(result, null, 2)}\n`, "utf8");
console.log(`Pinned Pi C12 safety-boundary observation written to ${output}`);
