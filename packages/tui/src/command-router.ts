import type { ToolPolicyMode } from "../../core/src/index.js";

export type TuiCommand =
  | { type: "help" }
  | { type: "exit" }
  | { type: "tools"; mode: Exclude<ToolPolicyMode, "custom"> }
  | { type: "sessions" }
  | { type: "unknown"; input: string };

export function parseTuiCommand(input: string): TuiCommand | null {
  const line = input.trim();
  if (!line.startsWith("/")) return null;

  if (line === "/help" || line === "/?") return { type: "help" };
  if (line === "/exit" || line === "/quit") return { type: "exit" };
  if (line === "/sessions") return { type: "sessions" };

  if (line.startsWith("/tools")) {
    const [, modeRaw] = line.split(/\s+/, 2);
    if (modeRaw === "none" || modeRaw === "default" || modeRaw === "full") {
      return { type: "tools", mode: modeRaw };
    }
    return { type: "unknown", input: line };
  }

  return { type: "unknown", input: line };
}

export function helpText(): string {
  return [
    "commands:",
    "  /help                 show commands",
    "  /sessions             list local ipi sessions",
    "  /tools none|default|full",
    "  /exit                 quit",
  ].join("\n");
}
