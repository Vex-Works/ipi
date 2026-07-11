import readline from "node:readline";
import { stdin, stdout } from "node:process";
import { AgentRunner, createInitialAppState, createToolPolicy, openWorkspace, SessionStore, type AppState, type RunEvent } from "../../core/src/index.js";
import { sanitizeTerminalLine } from "./terminal-sanitize.js";

const ESC = "\x1b[";

function clear(): void { stdout.write("\x1b[2J\x1b[H"); }
function hideCursor(): void { stdout.write("\x1b[?25l"); }
function showCursor(): void { stdout.write("\x1b[?25h"); }
function altScreen(): void { stdout.write("\x1b[?1049h"); }
function mainScreen(): void { stdout.write("\x1b[?1049l"); }
function move(row: number, col: number): void { stdout.write(`${ESC}${row};${col}H`); }
function style(code: string): string { return `${ESC}${code}m`; }
function stripAnsi(s: string): string { return s.replace(/\x1b\[[0-9;]*m/g, ""); }
function clip(s: string, width: number): string {
  const raw = stripAnsi(s);
  if (raw.length <= width) return s + " ".repeat(Math.max(0, width - raw.length));
  return raw.slice(0, Math.max(0, width - 1)) + "…";
}
function line(width: number, char = "─"): string { return char.repeat(Math.max(0, width)); }
function boxTitle(title: string, width: number): string { return ` ${title} ${line(Math.max(0, width - title.length - 2))}`; }
function now(): string { return new Date().toISOString(); }

export class IpiScreenApp {
  private readonly runner = new AgentRunner();
  private readonly sessions = new SessionStore();
  private state: AppState = createInitialAppState();
  private input = "";
  private status = "ready";
  private shouldExit = false;

  constructor(private readonly workspacePath = process.cwd()) {
    this.runner.events.subscribe((event) => {
      this.state.events.push(event);
      this.render();
    });
  }

  async start(): Promise<void> {
    this.state.workspace = await openWorkspace(this.workspacePath);
    this.state.session = await this.sessions.create(this.state.workspace);
    await this.runner.start({ workspace: this.state.workspace, tools: this.state.tools });

    altScreen();
    hideCursor();
    readline.emitKeypressEvents(stdin);
    if (stdin.isTTY) stdin.setRawMode(true);
    stdout.on("resize", () => this.render());
    stdin.on("keypress", (_str, key) => void this.handleKey(key));
    this.render();
  }

  private async handleKey(key: readline.Key): Promise<void> {
    if (this.shouldExit) return;
    if (key.ctrl && key.name === "c") return this.exit();
    if (key.name === "escape") return this.exit();
    if (key.name === "backspace") {
      this.input = this.input.slice(0, -1);
      return this.render();
    }
    if (key.name === "return") {
      const text = this.input.trim();
      this.input = "";
      await this.submit(text);
      return this.render();
    }
    if (key.sequence && key.sequence >= " " && !key.ctrl && !key.meta) {
      this.input += key.sequence;
      return this.render();
    }
  }

  private async submit(text: string): Promise<void> {
    if (!text) return;
    if (text === "/exit" || text === "/quit") return this.exit();
    if (text === "/help") {
      this.status = "commands: /help /sessions /tools none|default|full /exit";
      return;
    }
    if (text === "/sessions") {
      const sessions = await this.sessions.list();
      this.status = `${sessions.length} local sessions`;
      return;
    }
    if (text.startsWith("/tools ")) {
      const mode = text.slice(7).trim();
      if (mode === "none" || mode === "default" || mode === "full") {
        this.state.tools = createToolPolicy(mode);
        this.status = `tools set to ${mode}`;
      } else {
        this.status = "usage: /tools none|default|full";
      }
      return;
    }
    await this.runner.send(text);
    await this.persistSession();
  }

  private async persistSession(): Promise<void> {
    if (!this.state.session) return;
    await this.sessions.save({ ...this.state.session, events: this.state.events });
  }

  private exit(): void {
    this.shouldExit = true;
    showCursor();
    mainScreen();
    if (stdin.isTTY) stdin.setRawMode(false);
    process.exit(0);
  }

  private render(): void {
    const width = stdout.columns || 100;
    const height = stdout.rows || 30;
    const railW = Math.min(24, Math.max(18, Math.floor(width * 0.2)));
    const inspectorW = Math.min(34, Math.max(24, Math.floor(width * 0.28)));
    const timelineW = Math.max(30, width - railW - inspectorW - 2);
    const bodyTop = 3;
    const composerTop = height - 3;
    const bodyH = Math.max(5, composerTop - bodyTop);

    clear();
    move(1, 1); stdout.write(style("1;38;5;147") + "ipi" + style("0m") + "  ");
    stdout.write(clip(`${sanitizeTerminalLine(this.state.workspace?.name ?? "no workspace")}  ·  tools ${this.state.tools.mode}  ·  ${sanitizeTerminalLine(this.status)}`, width - 7));
    move(2, 1); stdout.write(style("38;5;238") + line(width) + style("0m"));

    this.renderRail(bodyTop, 1, railW, bodyH);
    this.renderTimeline(bodyTop, railW + 2, timelineW, bodyH);
    this.renderInspector(bodyTop, railW + timelineW + 3, inspectorW, bodyH);

    move(composerTop, 1); stdout.write(style("38;5;238") + line(width) + style("0m"));
    move(composerTop + 1, 1); stdout.write(style("38;5;244") + "prompt " + style("0m") + clip(sanitizeTerminalLine(this.input), width - 8));
    move(composerTop + 2, 1); stdout.write(style("38;5;240") + "Enter send · /help commands · Esc quit" + style("0m"));
  }

  private renderRail(row: number, col: number, w: number, h: number): void {
    const items = ["Workspace", `  ${sanitizeTerminalLine(this.state.workspace?.name ?? "-")}`, "", "Sessions", "Files", "Search", "Skills", "Models", "Settings"];
    move(row, col); stdout.write(style("38;5;244") + boxTitle("workspace", w) + style("0m"));
    for (let i = 0; i < h - 1; i++) {
      move(row + 1 + i, col);
      const item = items[i] ?? "";
      const active = item === "Sessions";
      stdout.write((active ? style("38;5;147") : style("38;5;250")) + clip(item, w) + style("0m"));
    }
  }

  private renderTimeline(row: number, col: number, w: number, h: number): void {
    move(row, col); stdout.write(style("38;5;244") + boxTitle("timeline", w) + style("0m"));
    const lines: string[] = [];
    for (const event of this.state.events) {
      if (event.type === "state") lines.push(style("38;5;244") + `[${sanitizeTerminalLine(event.label)}] ${sanitizeTerminalLine(event.detail ?? "")}` + style("0m"));
      if (event.type === "user-message") lines.push(style("38;5;111") + "user" + style("0m"), `  ${sanitizeTerminalLine(event.text)}`);
      if (event.type === "assistant-message") lines.push(style("38;5;151") + "assistant" + style("0m"), `  ${sanitizeTerminalLine(event.text)}`);
    }
    const visible = lines.slice(-(h - 1));
    for (let i = 0; i < h - 1; i++) {
      move(row + 1 + i, col);
      stdout.write(clip(visible[i] ?? "", w));
    }
  }

  private renderInspector(row: number, col: number, w: number, h: number): void {
    move(row, col); stdout.write(style("38;5;244") + boxTitle("inspector", w) + style("0m"));
    const latest = this.state.events[this.state.events.length - 1];
    const rows = [
      `session ${this.state.session?.id.slice(0, 8) ?? "-"}`,
      `events  ${this.state.events.length}`,
      `updated ${now().slice(11, 19)}`,
      "",
      latest ? `selected ${latest.type}` : "selected -",
      "",
      "agent adapter: pending",
      "diff review: pending",
      "native desktop: later",
    ];
    for (let i = 0; i < h - 1; i++) {
      move(row + 1 + i, col);
      stdout.write(style("38;5;250") + clip(rows[i] ?? "", w) + style("0m"));
    }
  }
}
