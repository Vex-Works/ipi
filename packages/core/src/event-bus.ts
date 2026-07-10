import type { RunEvent } from "./types.js";

export type RunEventListener = (event: RunEvent) => void;

export class RunEventBus {
  private listeners = new Set<RunEventListener>();

  subscribe(listener: RunEventListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(event: RunEvent): void {
    for (const listener of this.listeners) listener(event);
  }
}
