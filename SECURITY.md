# ipi security model

ipi runs local agent code with the permissions of the current Windows user. Tool
approval is an application control boundary; it is not an operating-system
sandbox.

## Workspace trust

Project-owned Pi extensions and project packages are disabled by default. Opening
a folder does not make code inside that folder trusted. Until ipi ships an
explicit repository-trust flow, only user-level runtime configuration and
packages are loaded by the desktop bridge.

The Pi runtime entry point is resolved by the native application and passed to
the Node bridge explicitly. Production bridges never discover a runtime from a
workspace `node_modules` directory.

## Tool policy

- **Ask first** allows reads inside the workspace and asks before mutations,
  shell commands, unknown tools, or reads outside the workspace.
- **Auto approve** may allow known, path-verified tools. Unknown tools still ask.
- **Read only** hard-blocks shell and mutating tools.
- Custom rules fail closed when a tool or path cannot be verified.
- Sensitive-looking paths and bulk content searches still ask in every mode
  except explicit full-auto access.
- A run-level approval is scoped to the same normalized command, path, or full
  request fingerprint; approving one shell request never approves every shell
  command in that run.

Path checks resolve symbolic links and Windows junctions before comparing a tool
target with the workspace boundary.

## Runtime and packages

The managed runtime installer uses a tested, exact Pi version. Explicit user
runtime overrides remain possible. Runtime updates must remain inside ipi-managed
storage and verify ownership before replacing or deleting managed directories.
Project workspaces are never searched for the Pi runtime implementation.

Third-party packages and extensions are executable code. Install only sources you
trust, even when npm lifecycle scripts are disabled.

Project-scoped package actions remain disabled and existing project package
entries are shown read-only until ipi has an explicit workspace-trust workflow.
Managed runtime and application updates are prepared in staging directories,
validated, and switched by directory rename with rollback data retained until
the updated application starts.

## Terminal output

The experimental TUI removes terminal control protocols and bidirectional text
overrides from untrusted output. Full-screen single-line regions also collapse
line movement characters so model or workspace text cannot redraw surrounding
interface rows.

## Secrets and provider data

Prefer environment-variable references for provider credentials. Remote custom
endpoints must use HTTPS; plain HTTP is limited to loopback development endpoints.
Sensitive-looking attachments are path-only and are not embedded into a model
prompt automatically.

## Reporting a vulnerability

Do not include API keys, tokens, private session content, or credential files in a
report. Include the ipi version, Windows version, runtime version, reproduction
steps using non-sensitive sample data, and the relevant crash/setup log excerpt.
