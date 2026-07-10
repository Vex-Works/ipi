# ipi Runtime Setup

ipi can work with an existing local Pi runtime, or it can initialize an ipi-managed runtime during first-run setup.

The goal is simple: launch ipi, confirm the setup plan, choose your provider, and start using a local agent without manually installing Pi globally.

## First-run setup

If ipi cannot find a usable local Pi runtime, it opens setup instead of failing silently.

Setup can:

1. show what it is about to download or install;
2. download portable Node.js from `nodejs.org` when needed;
3. verify the Node archive against Node's official checksum file;
4. install the upstream `@earendil-works/pi-coding-agent` package from npm;
5. create local agent folders and configuration;
6. continue to provider/model onboarding.

Setup requires confirmation before downloading or installing anything.

## What setup does not do

ipi setup does **not**:

- install Pi globally;
- modify system PATH;
- require administrator rights;
- store API keys;
- copy browser cookies, SSH keys, `.env` files, wallets, or other secrets;
- run remote install scripts.

## Existing runtimes

If you already have a local Pi runtime, ipi can use it directly.

Advanced users can point ipi at a custom runtime from Settings or with supported environment/config overrides. Most users should not need to edit runtime files manually.

## Providers and authentication

Runtime setup only prepares the local agent environment. Model responses still require provider configuration.

ipi supports provider setup for:

- Codex OAuth authentication;
- Anthropic/Claude;
- common API-key providers;
- OpenAI-compatible local or hosted endpoints.

ipi does not recommend or preselect a provider.

## Diagnostics

Settings includes runtime diagnostics for checking local readiness state.

Diagnostics are meant to help answer questions like:

- Is Node available?
- Is the Pi runtime available?
- Are agent settings and model registry files present?
- Is the session store ready?
- Are package/plugin folders available?

Diagnostics may show local paths. Review screenshots before sharing them publicly.

## Troubleshooting

If setup or chat does not work:

1. open Settings → Runtime diagnostics;
2. check the visible error message;
3. confirm whether you are using an existing runtime or the ipi-managed runtime;
4. confirm that provider/model/auth settings are configured;
5. include the visible diagnostic error when reporting an issue.

Do not include API keys, tokens, `.env` files, browser cookies, private SSH keys, wallet files, or other secrets in bug reports.
