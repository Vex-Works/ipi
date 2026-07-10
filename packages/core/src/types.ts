export type ID = string;

export interface Workspace {
  id: ID;
  rootPath: string;
  name: string;
  trusted: boolean;
}

export interface Session {
  id: ID;
  workspaceId: ID;
  title?: string;
  createdAt: string;
  updatedAt: string;
  activeBranchId?: string;
}

export interface ModelRef {
  provider: string;
  modelId: string;
}

export type ToolPolicyMode = "none" | "default" | "full" | "custom";

export interface ToolPolicy {
  mode: ToolPolicyMode;
  allowedTools: string[];
}

export type RunEvent =
  | { type: "user-message"; id: ID; text: string; timestamp: string }
  | { type: "assistant-message"; id: ID; text: string; timestamp: string; streaming?: boolean }
  | { type: "tool-call"; id: ID; tool: string; input: unknown; timestamp: string }
  | { type: "tool-result"; id: ID; toolCallId: ID; output: unknown; error?: string; timestamp: string }
  | { type: "file-diff"; id: ID; path: string; diff: string; timestamp: string }
  | { type: "command-output"; id: ID; command: string; output: string; exitCode?: number; timestamp: string }
  | { type: "state"; id: ID; label: string; detail?: string; timestamp: string };

export interface ComposerState {
  text: string;
  attachments: string[];
  mode: "idle" | "running" | "steering";
}

export interface AppState {
  workspace?: Workspace;
  session?: Session;
  events: RunEvent[];
  selectedEventId?: ID;
  running: boolean;
  model?: ModelRef;
  tools: ToolPolicy;
  composer: ComposerState;
}
