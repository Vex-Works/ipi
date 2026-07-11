import path from "node:path";
import { stat } from "node:fs/promises";
import type { Workspace } from "./types.js";

function workspaceId(rootPath: string): string {
  return Buffer.from(rootPath).toString("base64url");
}

export async function openWorkspace(inputPath = process.cwd()): Promise<Workspace> {
  const rootPath = path.resolve(inputPath);
  const info = await stat(rootPath);
  if (!info.isDirectory()) throw new Error(`Workspace is not a directory: ${rootPath}`);

  return {
    id: workspaceId(rootPath),
    rootPath,
    name: path.basename(rootPath) || rootPath,
    // Trust must be granted by a user-facing workflow. Merely opening a path is
    // never evidence that project-owned code or configuration is safe to run.
    trusted: false,
  };
}
