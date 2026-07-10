#!/usr/bin/env node
import { IpiTuiApp } from "../../tui/src/app.js";
import { IpiScreenApp } from "../../tui/src/screen.js";

function parseArgs(argv: string[]): { workspacePath?: string; plain: boolean } {
  const plain = argv.includes("--plain");
  const positional = argv.filter((arg) => !arg.startsWith("-"));
  return { workspacePath: positional[0], plain };
}

const args = parseArgs(process.argv.slice(2));
const App = args.plain ? IpiTuiApp : IpiScreenApp;

new App(args.workspacePath).start().catch((error) => {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
});
