import assert from "node:assert/strict";
import { mkdtemp, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { SessionStore, type StoredSession } from "./session-store.js";
import type { Workspace } from "./types.js";

test("session saves replace cleanly and malformed records are ignored", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ipi-session-store-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const store = new SessionStore(directory);
  const workspace: Workspace = { id: "workspace", rootPath: directory, name: "test", trusted: false };
  const session = await store.create(workspace);

  await store.save({ ...session, title: "updated", events: [] });
  assert.equal((await store.load(session.id)).title, "updated");

  await writeFile(path.join(directory, "corrupt.json"), "{not json", "utf8");
  await writeFile(path.join(directory, "invalid-event.json"), JSON.stringify({
    ...session,
    id: "invalid-event",
    events: [null, { type: "invented", id: "bad", timestamp: new Date().toISOString() }],
  }), "utf8");
  assert.deepEqual((await store.list()).map((item) => item.id), [session.id]);
  await assert.rejects(store.load("invalid-event"), /Invalid session record/);
  assert.equal((await readdir(directory)).some((file) => file.endsWith(".tmp")), false);
});

test("session ids cannot escape the session directory", async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "ipi-session-store-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const store = new SessionStore(directory);
  const invalid = {
    id: "../outside",
    workspaceId: "workspace",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    events: [],
  } as StoredSession;

  await assert.rejects(store.save(invalid), /Invalid session id/);
  await assert.rejects(store.load("../outside"), /Invalid session id/);
});
