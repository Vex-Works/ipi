import { mkdir, readFile, readdir, rename, rm, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import path from "node:path";
import os from "node:os";
import type { RunEvent, Session, Workspace } from "./types.js";

export interface StoredSession extends Session {
  events: RunEvent[];
}

const SESSION_ID_PATTERN = /^[A-Za-z0-9_-]{1,128}$/;

function assertSessionId(id: unknown): asserts id is string {
  if (typeof id !== "string" || !SESSION_ID_PATTERN.test(id)) {
    throw new Error("Invalid session id");
  }
}

function isOptionalString(value: unknown): value is string | undefined {
  return value === undefined || typeof value === "string";
}

function isRunEvent(value: unknown): value is RunEvent {
  if (!value || typeof value !== "object") return false;
  const event = value as Record<string, unknown>;
  if (typeof event.type !== "string" || typeof event.id !== "string" || typeof event.timestamp !== "string") return false;

  switch (event.type) {
    case "user-message":
      return typeof event.text === "string";
    case "assistant-message":
      return typeof event.text === "string" && (event.streaming === undefined || typeof event.streaming === "boolean");
    case "tool-call":
      return typeof event.tool === "string" && Object.hasOwn(event, "input");
    case "tool-result":
      return typeof event.toolCallId === "string" && Object.hasOwn(event, "output") && isOptionalString(event.error);
    case "file-diff":
      return typeof event.path === "string" && typeof event.diff === "string";
    case "command-output":
      return typeof event.command === "string" && typeof event.output === "string" &&
        (event.exitCode === undefined || typeof event.exitCode === "number" && Number.isFinite(event.exitCode));
    case "state":
      return typeof event.label === "string" && isOptionalString(event.detail);
    default:
      return false;
  }
}

function parseStoredSession(raw: string, expectedId?: string): StoredSession {
  const value: unknown = JSON.parse(raw);
  if (!value || typeof value !== "object") throw new Error("Invalid session record");
  const candidate = value as Partial<StoredSession>;
  assertSessionId(candidate.id);
  if (expectedId && candidate.id !== expectedId) throw new Error("Session id does not match its file name");
  if (typeof candidate.workspaceId !== "string" ||
      typeof candidate.createdAt !== "string" ||
      typeof candidate.updatedAt !== "string" ||
      !isOptionalString(candidate.title) ||
      !isOptionalString(candidate.activeBranchId) ||
      !Array.isArray(candidate.events) ||
      !candidate.events.every(isRunEvent)) {
    throw new Error("Invalid session record");
  }
  return candidate as StoredSession;
}

export class SessionStore {
  constructor(private readonly baseDir = path.join(os.homedir(), ".ipi", "sessions")) {}

  async create(workspace: Workspace): Promise<StoredSession> {
    const timestamp = new Date().toISOString();
    const session: StoredSession = {
      id: randomUUID(),
      workspaceId: workspace.id,
      title: "New session",
      createdAt: timestamp,
      updatedAt: timestamp,
      events: [],
    };
    await this.save(session);
    return session;
  }

  async save(session: StoredSession): Promise<void> {
    assertSessionId(session.id);
    await mkdir(this.baseDir, { recursive: true });
    const next = { ...session, updatedAt: new Date().toISOString() };
    const target = this.pathFor(session.id);
    const temporary = `${target}.${process.pid}.${randomUUID()}.tmp`;
    try {
      await writeFile(temporary, JSON.stringify(next, null, 2), "utf8");
      await rename(temporary, target);
    } catch (error) {
      await rm(temporary, { force: true }).catch(() => undefined);
      throw error;
    }
  }

  async load(id: string): Promise<StoredSession> {
    assertSessionId(id);
    const raw = await readFile(this.pathFor(id), "utf8");
    return parseStoredSession(raw, id);
  }

  async list(): Promise<StoredSession[]> {
    await mkdir(this.baseDir, { recursive: true });
    const files = await readdir(this.baseDir);
    const loaded = await Promise.allSettled(
      files
        .filter((file) => file.endsWith(".json"))
        .map((file) => {
          const expectedId = file.slice(0, -".json".length);
          return readFile(path.join(this.baseDir, file), "utf8").then((raw) => parseStoredSession(raw, expectedId));
        })
    );
    const sessions = loaded
      .filter((result): result is PromiseFulfilledResult<StoredSession> => result.status === "fulfilled")
      .map((result) => result.value);
    return sessions.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }

  private pathFor(id: string): string {
    assertSessionId(id);
    return path.join(this.baseDir, `${id}.json`);
  }
}
