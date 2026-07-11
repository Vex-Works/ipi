import type { RunEvent } from "../../../core/src/index.js";
import { sanitizeTerminalLine, sanitizeTerminalMultiline } from "../terminal-sanitize.js";

function indent(text: string, spaces = 2): string {
  const pad = " ".repeat(spaces);
  return text.split("\n").map((line) => `${pad}${line}`).join("\n");
}

export function renderRunEvent(event: RunEvent): string {
  switch (event.type) {
    case "user-message":
      return `\nuser\n${indent(sanitizeTerminalMultiline(event.text))}`;
    case "assistant-message":
      return `\nassistant\n${indent(sanitizeTerminalMultiline(event.text))}\n`;
    case "tool-call":
      return `\ntool ${sanitizeTerminalLine(event.tool)}\n${indent(sanitizeTerminalMultiline(JSON.stringify(event.input, null, 2)))}`;
    case "tool-result":
      return `\nresult ${sanitizeTerminalLine(event.toolCallId)}${event.error ? " error" : ""}\n${indent(sanitizeTerminalMultiline(typeof event.output === "string" ? event.output : JSON.stringify(event.output, null, 2)))}`;
    case "file-diff":
      return `\ndiff ${sanitizeTerminalLine(event.path)}\n${indent(sanitizeTerminalMultiline(event.diff))}`;
    case "command-output":
      return `\n$ ${sanitizeTerminalLine(event.command)}\n${indent(sanitizeTerminalMultiline(event.output))}${event.exitCode === undefined ? "" : `\n  exit ${event.exitCode}`}`;
    case "state":
      return `[${sanitizeTerminalLine(event.label)}] ${sanitizeTerminalLine(event.detail ?? "")}`;
  }
}
