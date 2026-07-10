import type { ModelRef, RunEvent, ToolPolicy, Workspace } from "./types.js";
import { RunEventBus } from "./event-bus.js";

function now(): string {
  return new Date().toISOString();
}

function id(prefix: string): string {
  return `${prefix}_${crypto.randomUUID().slice(0, 8)}`;
}

export interface StartRunInput {
  workspace: Workspace;
  model?: ModelRef;
  tools: ToolPolicy;
}

export class AgentRunner {
  readonly events = new RunEventBus();
  private running = false;

  async start(input: StartRunInput): Promise<void> {
    this.events.emit({
      type: "state",
      id: id("state"),
      label: "workspace opened",
      detail: `${input.workspace.name} · tools ${input.tools.mode}`,
      timestamp: now(),
    });
  }

  async send(message: string): Promise<void> {
    if (this.running) throw new Error("A run is already active");
    this.running = true;

    this.emit({ type: "user-message", id: id("user"), text: message, timestamp: now() });
    this.emit({ type: "state", id: id("state"), label: "agent placeholder", detail: "Real agent integration comes next.", timestamp: now() });
    this.emit({
      type: "assistant-message",
      id: id("assistant"),
      text: `I received: ${message}`,
      timestamp: now(),
    });

    this.running = false;
  }

  async abort(): Promise<void> {
    if (!this.running) return;
    this.running = false;
    this.emit({ type: "state", id: id("state"), label: "aborted", timestamp: now() });
  }

  private emit(event: RunEvent): void {
    this.events.emit(event);
  }
}
