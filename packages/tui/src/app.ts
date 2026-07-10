import readline from "node:readline/promises";
import { stdin as input, stdout as output } from "node:process";
import { pathToFileURL } from "node:url";
import {
  AgentRunner,
  SessionStore,
  createInitialAppState,
  createToolPolicy,
  openWorkspace,
  type AppState,
  type RunEvent,
} from "../../core/src/index.js";
import { parseTuiCommand, helpText } from "./command-router.js";
import { renderRunEvent } from "./views/timeline.js";

export class IpiTuiApp {
  private readonly runner = new AgentRunner();
  private readonly sessions = new SessionStore();

  state: AppState = createInitialAppState();

  constructor(private readonly workspacePath = process.cwd()) {
    this.runner.events.subscribe((event) => {
      this.state.events.push(event);
      this.renderEvent(event);
    });
  }

  async start(): Promise<void> {
    const workspace = await openWorkspace(this.workspacePath);
    const session = await this.sessions.create(workspace);
    this.state.workspace = workspace;
    this.state.session = session;

    this.printHeader();
    await this.runner.start({ workspace, tools: this.state.tools });
    await this.inputLoop();
  }

  private async inputLoop(): Promise<void> {
    const rl = readline.createInterface({ input, output });
    try {
      while (true) {
        const answer = await rl.question("ipi> ").catch(() => null);
        if (answer === null) break;
        const line = answer.trim();
        if (!line) continue;
        const command = parseTuiCommand(line);
        if (command) {
          if (command.type === "exit") break;
          if (command.type === "help") console.log(helpText());
          if (command.type === "sessions") await this.printSessions();
          if (command.type === "tools") {
            this.state.tools = createToolPolicy(command.mode);
            console.log(`tools: ${command.mode} (${this.state.tools.allowedTools.join(", ") || "none"})`);
          }
          if (command.type === "unknown") console.log(`unknown command: ${command.input}`);
          continue;
        }
        await this.runner.send(line);
        await this.persistSession();
      }
    } finally {
      rl.close();
    }
  }

  private printHeader(): void {
    console.log("ipi");
    console.log(`workspace: ${this.state.workspace?.rootPath}`);
    console.log("type /help for commands, /exit to quit\n");
  }

  private async printSessions(): Promise<void> {
    const sessions = await this.sessions.list();
    if (sessions.length === 0) {
      console.log("no sessions yet");
      return;
    }
    for (const session of sessions.slice(0, 12)) {
      console.log(`${session.id.slice(0, 8)}  ${session.updatedAt}  ${session.title ?? "Untitled"}`);
    }
  }

  private async persistSession(): Promise<void> {
    if (!this.state.session) return;
    await this.sessions.save({ ...this.state.session, events: this.state.events });
  }

  private renderEvent(event: RunEvent): void {
    console.log(renderRunEvent(event));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const workspacePath = process.argv[2] ?? process.cwd();
  new IpiTuiApp(workspacePath).start().catch((error) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}
