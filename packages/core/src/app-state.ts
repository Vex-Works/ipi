import type { AppState, RunEvent } from "./types.js";
import { createToolPolicy } from "./tool-policy.js";

export function createInitialAppState(): AppState {
  return {
    events: [],
    running: false,
    tools: createToolPolicy("default"),
    composer: { text: "", attachments: [], mode: "idle" },
  };
}

export function reduceRunEvent(state: AppState, event: RunEvent): AppState {
  return {
    ...state,
    events: [...state.events, event],
    running: event.type === "assistant-message" && event.streaming ? true : state.running,
  };
}
