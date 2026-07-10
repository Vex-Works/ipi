# ipi Windows Installer Scripts

This folder contains the NSIS installer definition for ipi.

## What it creates

When NSIS is available, the script can create:

```text
dist/ipi-Setup-<version>-win-x64.exe
```

The installer is per-user and does not require admin rights by default.

Installer behavior:

- lets the user choose the install directory;
- copies the published ipi app files to that directory;
- creates Start Menu shortcuts;
- creates a Desktop shortcut;
- writes a per-user uninstall entry under HKCU;
- launches `ipi.exe --setup --post-install` after install when the user keeps the finish-page option enabled; setup shows the runtime plan and only downloads/installs Node/Pi after user confirmation.

## Build

Requires `makensis.exe` from NSIS. The script does not download or install NSIS.

By default the installer stages a self-contained `win-x64` publish so end users do not need to install the .NET Desktop Runtime separately. Pass `-FrameworkDependent` only for development builds.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/installer/New-IpiNsisInstaller.ps1
```

If NSIS is not on PATH:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/installer/New-IpiNsisInstaller.ps1 -MakensisPath "C:\Program Files (x86)\NSIS\makensis.exe"
```

## Safety

Verify generated installers in an isolated test folder before using a real install location. Running the installer creates shortcuts and HKCU uninstall metadata.

The uninstaller deletes the files generated from the publish stage and removes empty directories; it does not recursively delete the entire selected install directory.
