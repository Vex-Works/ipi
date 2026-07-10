import type { ToolPolicy } from "./types.js";

export const TOOL_PRESETS = {
  none: [] as string[],
  default: ["read", "edit", "write", "bash"] as string[],
  full: ["read", "edit", "write", "bash", "grep", "find", "ls"] as string[],
};

export function createToolPolicy(mode: "none" | "default" | "full"): ToolPolicy {
  return { mode, allowedTools: [...TOOL_PRESETS[mode]] };
}
