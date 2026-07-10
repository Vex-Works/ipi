import type { RunEvent } from "../../../core/src/index.js";

function indent(text: string, spaces = 2): string {
  const pad = " ".repeat(spaces);
  return text.split("\n").map((line) => `${pad}${line}`).join("\n");
}

export function renderRunEvent(event: RunEvent): string {
  switch (event.type) {
    case "user-message":
      return `\nuser\n${indent(event.text)}`;
    case "assistant-message":
      return `\nassistant\n${indent(event.text)}\n`;
    case "tool-call":
      return `\ntool ${event.tool}\n${indent(JSON.stringify(event.input, null, 2))}`;
    case "tool-result":
      return `\nresult ${event.toolCallId}${event.error ? " error" : ""}\n${indent(typeof event.output === "string" ? event.output : JSON.stringify(event.output, null, 2))}`;
    case "file-diff":
      return `\ndiff ${event.path}\n${indent(event.diff)}`;
    case "command-output":
      return `\n$ ${event.command}\n${indent(event.output)}${event.exitCode === undefined ? "" : `\n  exit ${event.exitCode}`}`;
    case "state":
      return `[${event.label}] ${event.detail ?? ""}`;
  }
}
