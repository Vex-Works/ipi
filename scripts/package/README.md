# ipi Packaging Scripts

## Portable release

Create a portable Windows zip without installing extra tooling. By default it uses a self-contained `win-x64` publish so end users do not need to install the .NET Desktop Runtime separately:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package/New-IpiPortableRelease.ps1
```

Outputs:

- `dist/ipi-portable-<version>-win-x64/`
- `dist/ipi-portable-<version>-win-x64.zip`
- `dist/ipi-portable-<version>-win-x64.zip.sha256`

The portable package contains:

- `Setup ipi.cmd` → launches `ipi/ipi.exe --setup`
- `Start ipi.cmd` → launches `ipi/ipi.exe`
- `ipi/` → published WPF app files
- `ipi-portable-manifest.json` → package metadata

The setup flow can install/copy the app files to a user-selected folder and then launch that installed copy. On first run it detects an existing local Pi runtime; if missing, it can download portable Node.js and install the upstream Pi npm package into ipi-managed AppData folders after user confirmation.

## Notes

This is not yet the final Windows installer. It does not create Start Menu shortcuts, Desktop shortcuts, or an uninstall entry. Those belong to the later installer stage.
