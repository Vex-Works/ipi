import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import type { RunEvent, Session, Workspace } from "./types.js";

export interface StoredSession extends Session {
  events: RunEvent[];
}

export class SessionStore {
  constructor(private readonly baseDir = path.join(os.homedir(), ".ipi", "sessions")) {}

  async create(workspace: Workspace): Promise<StoredSession> {
    const timestamp = new Date().toISOString();
    const session: StoredSession = {
      id: crypto.randomUUID(),
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
    await mkdir(this.baseDir, { recursive: true });
    const next = { ...session, updatedAt: new Date().toISOString() };
    await writeFile(this.pathFor(session.id), JSON.stringify(next, null, 2), "utf8");
  }

  async load(id: string): Promise<StoredSession> {
    const raw = await readFile(this.pathFor(id), "utf8");
    return JSON.parse(raw) as StoredSession;
  }

  async list(): Promise<StoredSession[]> {
    await mkdir(this.baseDir, { recursive: true });
    const files = await readdir(this.baseDir);
    const sessions = await Promise.all(
      files
        .filter((file) => file.endsWith(".json"))
        .map((file) => readFile(path.join(this.baseDir, file), "utf8").then((raw) => JSON.parse(raw) as StoredSession))
    );
    return sessions.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
  }

  private pathFor(id: string): string {
    return path.join(this.baseDir, `${id}.json`);
  }
}
