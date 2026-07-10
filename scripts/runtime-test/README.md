# ipi Runtime Test Scripts

These scripts are for contributor runtime testing without touching the user's real Pi installation.

They do not download packages, install system dependencies, edit PATH, or modify the real Pi agent directory.

## 1. Create an isolated sandbox runtime

```powershell
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/New-IpiSandbox.ps1 -Reset -MinimalConfig
```

Default sandbox path:

```text
%TEMP%\ipi-runtime-sandbox
```

It creates:

```text
ipi-runtime-sandbox/
  pi-agent/
    settings.json        # only with -MinimalConfig; no default provider/model
    models.json
    sessions/
    skills/
    npm/node_modules/
  codex-skills/
```

## 2. Launch ipi against only that sandbox

```powershell
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/Start-IpiSandbox.ps1
```

This starts ipi with process-local environment variables:

```text
PI_AGENT_DIR=%TEMP%\ipi-runtime-sandbox\pi-agent
CODEX_SKILLS_DIR=%TEMP%\ipi-runtime-sandbox\codex-skills
```

The environment is restored after launching the child process.

## 3. Delete and retest

```powershell
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/Remove-IpiSandbox.ps1
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/New-IpiSandbox.ps1 -Reset
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/Start-IpiSandbox.ps1
```

Use this to simulate a fresh profile with no existing settings.

## 4. Portable app test

After publishing:

```powershell
dotnet publish apps/windows/Ipi.Desktop/Ipi.Desktop.csproj -c Release -r win-x64 --self-contained true
```

Create a portable test copy:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/runtime-test/New-IpiPortableTest.ps1 -Reset -WithBundledRuntime -Launch
```

Default output:

```text
%TEMP%\ipi-portable-test\app
```

With `-WithBundledRuntime`, the script creates:

```text
app/runtime/pi-agent
```

This simulates an app-shipped runtime folder.

## What this validates

| Scenario | Expected mode |
|---|---|
| `PI_AGENT_DIR` set by sandbox launcher | env |
| `app/runtime/pi-agent` exists | bundled |
| neither exists but real Pi exists | existing |
| empty runtime | setup/diagnostics should explain missing pieces |

## What this does not validate yet

- Actually downloading portable Node.js from nodejs.org.
- Actually running npm restore for the upstream Pi package.
- Windows installer behavior.

Those require explicit approval before running because they involve downloads, installs, or system-like changes.
