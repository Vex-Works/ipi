# ipi v0.1.0 Beta Release Notes

ipi is an independent, unofficial native Windows desktop app for local agent workflows, starting with Pi. It is not affiliated with or endorsed by Pi, Codex, Claude Code, OpenAI, Anthropic, Google, or any model provider.

This beta is focused on one thing: a clean, simple desktop foundation for local agent work.

## What this beta includes

- native Windows desktop workspace for projects, chats, approvals, settings, and diagnostics;
- clean, efficient local chat connected to the configured Pi runtime;
- first-run setup for users who do not already have a local Pi runtime;
- provider onboarding with no default recommendation or preselected provider;
- provider setup for Codex OAuth authentication, Anthropic/Claude, common API-key providers, and OpenAI-compatible endpoints;
- skills discovered from Pi, Codex, Claude Code-style folders, package folders, other local agent folders, and custom local paths;
- package/plugin management from Settings;
- installer and portable zip builds.

## Install options

### Installer

Use the `ipi-Setup-...-win-x64.exe` release asset.

Notes:

- The installer is currently unsigned. Windows SmartScreen may show an unknown publisher warning.
- Verify the SHA256 checksum shown in the GitHub release before running it.
- The installer lets you choose the install directory.
- The uninstaller removes ipi-installed files only; it should not recursively delete arbitrary user-selected directories.

### Portable zip

Use the `ipi-portable-...-win-x64.zip` release asset.

Notes:

- Extract to a user-writable folder.
- Start ipi from the included launcher or run `ipi.exe` directly.
- Run setup when first configuring runtime and provider access.

The Windows beta builds are self-contained, so most users should not need to install the .NET Desktop Runtime separately.

## Runtime setup

ipi can use an existing local Pi runtime. If it cannot find one, first-run setup can initialize an ipi-managed runtime after confirmation.

Setup can:

1. download portable Node.js from `nodejs.org`;
2. verify the Node archive against Node's official checksum file;
3. install the upstream `@earendil-works/pi-coding-agent` package from npm;
4. create local agent folders and config;
5. open provider/model onboarding.

Setup shows what it is about to do before downloading or installing anything.

It does **not** install Pi globally, modify PATH, require admin rights, or store API keys.

Advanced runtime layout details live in [`RUNTIME_BUNDLING.md`](RUNTIME_BUNDLING.md). Most users should not need to edit these files manually.

## Providers and auth

ipi does not recommend or preselect a model provider.

You can configure the provider you already use, including:

- Codex OAuth authentication;
- Anthropic/Claude;
- common API-key based providers;
- OpenAI-compatible local or hosted endpoints.

Model responses require valid provider, model, and auth configuration.

## Local data and privacy

ipi is local-first. It does not intentionally upload workspace files, settings, sessions, or secrets by itself.

Settings and diagnostics are designed to show local readiness state without exposing API keys or secret file contents. Diagnostics may show local paths; review screenshots before sharing them.

Do not paste API keys, tokens, `.env` files, browser cookies, wallet/seed files, or private SSH keys into issues or screenshots.

## Live run behavior

ipi keeps active agent runs scoped to the session that started them.

Expected behavior to verify before release:

- starting a new chat while another session is thinking should not leak old output into the new chat;
- switching away from a running session should not cancel that session's run;
- switching back to a running session should restore its live rows and pending approvals;
- the stop button should stop only the currently visible session run;
- tool approval cards should belong to the session that requested them.

## Known limitations

- The installer is unsigned.
- First-run setup requires network access when Node/Pi are not already present.
- Clean Windows install validation is still ongoing.
- Some package operations require local npm and network/package-registry access.
- Some provider-specific auth flows may still need refinement.
- Windows speech features depend on OS speech and microphone configuration.
- Acrylic/Mica behavior depends on Windows build, graphics, and system settings.
- macOS and Linux builds are planned, but this beta is Windows-only.

## Before reporting an issue

Please include:

- Windows version.
- Installer or portable build filename.
- Whether you used an existing runtime or first-run setup.
- The visible setup/diagnostics error message.
- Provider/auth type only, without secrets.

Screenshots are helpful, but review them first for paths, tokens, keys, account names, or private workspace details.
